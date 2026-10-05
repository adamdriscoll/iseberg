using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Security;

namespace Iseberg.Core;

internal sealed record WorkbenchHostServices(Func<ShowCommandRequest, string?> ShowCommand, Action<string> ShowCommandError,
    Func<bool> IsRunspacePushed);

internal sealed class WorkbenchHost : PSHost, IHostSupportsInteractiveSession
{
    private readonly Guid id = Guid.NewGuid();
    private readonly WorkbenchHostUi ui;
    private readonly PSObject privateData;
    private readonly Func<Runspace> currentRunspace;
    private readonly Func<bool> isPushed;
    private readonly Action<Runspace> push;
    private readonly Action pop;
    private readonly Action enterNested;
    private readonly Action exitNested;

    public WorkbenchHost(Action<OutputEntry> write, Func<InputRequest, string> read,
        Action<ProgressUpdate> progress, Action clear, Func<ShowCommandRequest, string?> showCommand,
        Func<Runspace> currentRunspace, Func<bool> isPushed, Action<Runspace> push, Action pop,
        Action enterNested, Action exitNested, Action<string> showCommandError)
    {
        ui = new(write, read, progress, clear);
        privateData = PSObject.AsPSObject(new WorkbenchHostServices(showCommand, showCommandError, isPushed));
        this.currentRunspace = currentRunspace;
        this.isPushed = isPushed;
        this.push = push;
        this.pop = pop;
        this.enterNested = enterNested;
        this.exitNested = exitNested;
    }

    public bool IsRunspacePushed => isPushed();
    public Runspace Runspace => currentRunspace();
    public void PushRunspace(Runspace runspace) => push(runspace);
    public void PopRunspace() => pop();
    public override PSObject PrivateData => privateData;

    public override Guid InstanceId => id;
    public override string Name => "Iseberg";
    public override Version Version => new(1, 0);
    public override PSHostUserInterface UI => ui;
    public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
    public override void SetShouldExit(int exitCode)
    {
        if (IsRunspacePushed) PopRunspace();
        else ui.WriteWarningLine($"The script requested exit ({exitCode}). The editor session remains open.");
    }
    public override void EnterNestedPrompt() => enterNested();
    public override void ExitNestedPrompt() => exitNested();
    public override void NotifyBeginApplication() =>
        ui.WriteWarningLine("Native output is redirected to the transcript; interactive terminal input is unavailable here. Use Start-IsebergTerminal for interactive applications.");
    public override void NotifyEndApplication() { }
}

internal sealed class WorkbenchHostUi(
    Action<OutputEntry> write, Func<InputRequest, string> read,
    Action<ProgressUpdate> progress, Action clear) : PSHostUserInterface, IHostUISupportsMultipleChoiceSelection
{
    private readonly WorkbenchRawUi raw = new(clear);
    public override bool SupportsVirtualTerminal => true;
    public override PSHostRawUserInterface RawUI => raw;
    public override string ReadLine() => read(new("PowerShell", "Enter a value:"));
    public override SecureString ReadLineAsSecureString() => ToSecure(read(new("PowerShell", "Enter a secure value:", true)));
    public override void Write(string value) => write(new(value, Style: raw.DefaultStyle));
    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) =>
        write(new(value, Style: new(AnsiOutputParser.ConsoleColorHex(foregroundColor), AnsiOutputParser.ConsoleColorHex(backgroundColor))));
    public override void WriteLine(string value) => Write(value + Environment.NewLine);
    public override void WriteErrorLine(string value) => write(new(value + Environment.NewLine, OutputKind.Error));
    public override void WriteWarningLine(string message) => write(new("WARNING: " + message + Environment.NewLine, OutputKind.Warning));
    public override void WriteVerboseLine(string message) => write(new("VERBOSE: " + message + Environment.NewLine, OutputKind.Verbose));
    public override void WriteDebugLine(string message) => write(new("DEBUG: " + message + Environment.NewLine, OutputKind.Debug));
    public override void WriteProgress(long sourceId, ProgressRecord record) =>
        progress(new(record.Activity, record.StatusDescription, record.PercentComplete, record.RecordType == ProgressRecordType.Completed));

    public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions)
    {
        var values = new Dictionary<string, PSObject>();
        foreach (var field in descriptions)
        {
            var secret = field.ParameterTypeFullName == typeof(SecureString).FullName;
            var input = read(new(caption, $"{message}\n{field.Name}: {field.HelpMessage}", secret));
            object value = secret ? ToSecure(input) : input;
            if (!secret && !string.IsNullOrEmpty(field.ParameterTypeFullName))
            {
                var type = Type.GetType(field.ParameterTypeFullName);
                if (type is not null)
                    value = LanguagePrimitives.ConvertTo(input, type, CultureInfo.CurrentCulture);
            }
            values[field.Name] = PSObject.AsPSObject(value);
        }
        return values;
    }

    public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
    {
        ValidateChoices(choices);
        if (defaultChoice < -1 || defaultChoice >= choices.Count)
            throw new ArgumentOutOfRangeException(nameof(defaultChoice));
        while (true)
        {
            var value = read(new(caption, message)
            {
                Choices = choices.Select(choice => new PromptChoice(choice.Label.Replace("&", ""), choice.HelpMessage)).ToArray(),
                DefaultChoices = defaultChoice >= 0 && defaultChoice < choices.Count ? [defaultChoice] : []
            });
            if (string.IsNullOrWhiteSpace(value) && defaultChoice >= 0 && defaultChoice < choices.Count)
                return defaultChoice;
            var index = ChoiceIndex(value, choices);
            if (index >= 0 && index < choices.Count) return index;
            WriteWarningLine("Enter a choice number or its label.");
        }
    }

    public Collection<int> PromptForChoice(string? caption, string? message, Collection<ChoiceDescription> choices,
        IEnumerable<int>? defaultChoices)
    {
        ValidateChoices(choices);
        var defaults = defaultChoices?.Distinct().ToArray() ?? [];
        if (defaults.Any(index => index < 0 || index >= choices.Count))
            throw new ArgumentOutOfRangeException(nameof(defaultChoices));
        while (true)
        {
            var value = read(new(caption ?? "PowerShell", message ?? "")
            {
                Choices = choices.Select(choice => new PromptChoice(choice.Label.Replace("&", ""), choice.HelpMessage)).ToArray(),
                MultipleChoice = true, DefaultChoices = defaults
            });
            if (value == "-") return [];
            if (string.IsNullOrWhiteSpace(value)) return new(defaults.ToList());
            var indices = new List<int>();
            foreach (var part in value.Split(','))
            {
                var index = ChoiceIndex(part.Trim(), choices);
                if (index < 0 || index >= choices.Count) { indices.Clear(); break; }
                if (!indices.Contains(index)) indices.Add(index);
            }
            if (indices.Count > 0) return new(indices);
            WriteWarningLine("Enter comma-separated choice numbers or labels.");
        }
    }

    private static void ValidateChoices(Collection<ChoiceDescription> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0) throw new ArgumentException("At least one choice is required.", nameof(choices));
    }

    private static int ChoiceIndex(string value, Collection<ChoiceDescription> choices)
    {
        if (int.TryParse(value, out var index)) return index;
        for (var i = 0; i < choices.Count; i++)
        {
            var label = choices[i].Label;
            var marker = label.IndexOf('&');
            if (string.Equals(value, label.Replace("&", ""), StringComparison.OrdinalIgnoreCase) ||
                marker >= 0 && marker + 1 < label.Length &&
                string.Equals(value, label.Substring(marker + 1, 1), StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) =>
        PromptForCredential(caption, message, userName, targetName, PSCredentialTypes.Default, PSCredentialUIOptions.Default);

    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName,
        PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
    {
        var name = read(new(caption, $"{message}\nUser name ({userName}):"));
        return new(string.IsNullOrWhiteSpace(name) ? userName : name, ToSecure(read(new(caption, "Password:", true))));
    }

    private static SecureString ToSecure(string value)
    {
        var secure = new SecureString();
        foreach (var character in value)
            secure.AppendChar(character);
        secure.MakeReadOnly();
        return secure;
    }
}

internal sealed class WorkbenchRawUi(Action clear) : PSHostRawUserInterface
{
    private ConsoleColor foreground = ConsoleColor.White;
    private ConsoleColor background = ConsoleColor.DarkBlue;
    private bool foregroundChanged;
    private bool backgroundChanged;
    internal OutputStyle? DefaultStyle => foregroundChanged || backgroundChanged
        ? new(foregroundChanged ? AnsiOutputParser.ConsoleColorHex(foreground) : null,
            backgroundChanged ? AnsiOutputParser.ConsoleColorHex(background) : null) : null;
    public override ConsoleColor ForegroundColor
    {
        get => foreground;
        set { _ = AnsiOutputParser.ConsoleColorHex(value); foreground = value; foregroundChanged = true; }
    }
    public override ConsoleColor BackgroundColor
    {
        get => background;
        set { _ = AnsiOutputParser.ConsoleColorHex(value); background = value; backgroundChanged = true; }
    }
    public override Coordinates CursorPosition { get => new(0, 0); set => throw Unsupported(); }
    public override Coordinates WindowPosition { get => new(0, 0); set => throw Unsupported(); }
    public override int CursorSize { get => 25; set => throw Unsupported(); }
    public override Size BufferSize { get => new(120, 3000); set => throw Unsupported(); }
    public override Size WindowSize { get => new(120, 40); set => throw Unsupported(); }
    public override Size MaxWindowSize => new(120, 40);
    public override Size MaxPhysicalWindowSize => MaxWindowSize;
    public override bool KeyAvailable => false;
    public override string WindowTitle { get; set; } = "Iseberg";
    public override void FlushInputBuffer() => throw Unsupported();
    public override KeyInfo ReadKey(ReadKeyOptions options) =>
        throw new PSNotSupportedException("This graphical host does not provide raw keyboard input. Use Read-Host.");
    public override BufferCell[,] GetBufferContents(Rectangle rectangle) =>
        throw new PSNotSupportedException("This graphical host does not expose a character-cell buffer.");
    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) =>
        throw new PSNotSupportedException("Character-cell output is not supported.");
    public override void SetBufferContents(Rectangle rectangle, BufferCell fill)
    {
        if (rectangle.Left == -1 && rectangle.Top == -1 && rectangle.Right == -1 && rectangle.Bottom == -1)
            clear();
        else
            throw new PSNotSupportedException("Character-cell output is not supported.");
    }
    public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) =>
        throw new PSNotSupportedException("Character-cell scrolling is not supported.");
    private static PSNotSupportedException Unsupported() =>
        new("This protected transcript has no terminal cursor, resizable character buffer or raw keyboard queue. Use Read-Host or Start-IsebergTerminal.");
}
