using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Security;

namespace Iseberg.Core;

internal sealed record WorkbenchHostServices(Func<ShowCommandRequest, string?> ShowCommand);

internal sealed class WorkbenchHost : PSHost, IHostSupportsInteractiveSession
{
    private readonly Guid id = Guid.NewGuid();
    private readonly WorkbenchHostUi ui;
    private readonly PSObject privateData;
    private readonly Func<Runspace> currentRunspace;
    private readonly Func<bool> isPushed;
    private readonly Action<Runspace> push;
    private readonly Action pop;

    public WorkbenchHost(Action<OutputEntry> write, Func<InputRequest, string> read,
        Action<ProgressUpdate> progress, Action clear, Func<ShowCommandRequest, string?> showCommand,
        Func<Runspace> currentRunspace, Func<bool> isPushed, Action<Runspace> push, Action pop)
    {
        ui = new(write, read, progress, clear);
        privateData = PSObject.AsPSObject(new WorkbenchHostServices(showCommand));
        this.currentRunspace = currentRunspace;
        this.isPushed = isPushed;
        this.push = push;
        this.pop = pop;
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
    public override void EnterNestedPrompt() => throw new PSNotSupportedException("Nested prompts are not supported. Use script breakpoints.");
    public override void ExitNestedPrompt() => throw new PSNotSupportedException("No nested prompt is active.");
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
}

internal sealed class WorkbenchHostUi(
    Action<OutputEntry> write, Func<InputRequest, string> read,
    Action<ProgressUpdate> progress, Action clear) : PSHostUserInterface
{
    private readonly WorkbenchRawUi raw = new(clear);
    public override PSHostRawUserInterface RawUI => raw;
    public override string ReadLine() => read(new("PowerShell", "Enter a value:"));
    public override SecureString ReadLineAsSecureString() => ToSecure(read(new("PowerShell", "Enter a secure value:", true)));
    public override void Write(string value) => write(new(value));
    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) => Write(value);
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
        var labels = string.Join(Environment.NewLine, choices.Select((c, i) => $"[{i}] {c.Label.Replace("&", "")}"));
        while (true)
        {
            var value = read(new(caption, $"{message}\n{labels}\nDefault: {defaultChoice}"));
            if (string.IsNullOrWhiteSpace(value) && defaultChoice >= 0 && defaultChoice < choices.Count)
                return defaultChoice;
            if (int.TryParse(value, out var index) && index >= 0 && index < choices.Count)
                return index;
            for (var i = 0; i < choices.Count; i++)
            {
                var label = choices[i].Label;
                var marker = label.IndexOf('&');
                if (string.Equals(value, label.Replace("&", ""), StringComparison.OrdinalIgnoreCase) ||
                    (marker >= 0 && marker + 1 < label.Length && string.Equals(value, label[(marker + 1)..(marker + 2)], StringComparison.OrdinalIgnoreCase)))
                    return i;
            }
            WriteWarningLine("Enter a choice number or its label.");
        }
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
    public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.White;
    public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.DarkBlue;
    public override Coordinates CursorPosition { get; set; }
    public override Coordinates WindowPosition { get; set; }
    public override int CursorSize { get; set; } = 25;
    public override Size BufferSize { get; set; } = new(120, 3000);
    public override Size WindowSize { get; set; } = new(120, 40);
    public override Size MaxWindowSize => new(240, 100);
    public override Size MaxPhysicalWindowSize => MaxWindowSize;
    public override bool KeyAvailable => false;
    public override string WindowTitle { get; set; } = "Iseberg";
    public override void FlushInputBuffer() { }
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
}
