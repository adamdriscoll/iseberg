using System.Management.Automation.Language;
using System.Text;
using Avalonia.Media;
using Devolutions.Terminal;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Settings;
using Iseberg.Core;

namespace Iseberg;

public sealed class TerminalOutputView
{
    private readonly ConsoleBuffer source;
    private readonly UserSettings settings;
    private OutputKind? lastKind;
    private OutputStyle? lastStyle;
    private bool lastWasCarriageReturn;
    public TermControl Control { get; }

    public TerminalOutputView(SessionModel session, UserSettings settings)
    {
        this.settings = settings;
        source = session.ClassicConsole;
        Control = new TermControl
        {
            Name = "DevolutionsOutput", AccessibleName = UiText.Get("TerminalOutput"),
            ConnectionFactory = _ => new OutputOnlyConnection()
        };
        // This connection does no IO: initialization completes synchronously without a child shell.
        Control.StartAsync(CreateProfile(settings), 120, 30).GetAwaiter().GetResult();
        Control.Engine.Feed("\x1b[?25l");
        Append(session.TerminalOutput.ToArray());
        source.OutputAppended += Append;
        source.Cleared += Clear;
    }

    public static ProfileSettings CreateProfile(UserSettings settings) => new()
    {
        Name = "Iseberg", Commandline = "Iseberg", StartingDirectory = Environment.CurrentDirectory,
        FontFace = settings.FontFamily, FontSize = settings.FontSize * 4 / 3 * settings.Zoom / 100,
        Foreground = DesktopTheme.HighContrast ? ((SolidColorBrush)DesktopTheme.Brush("WindowTextBrush")).Color.ToString() : settings.Theme.Colors["Console.Foreground"],
        Background = DesktopTheme.HighContrast ? ((SolidColorBrush)DesktopTheme.Brush("WindowBrush")).Color.ToString() : settings.Theme.Colors["Console.TextBackground"],
        SelectionBackground = ((SolidColorBrush)DesktopTheme.Brush("SelectionBrush")).Color.ToString(),
        HistorySize = 9001, Padding = "4", CloseOnExit = CloseOnExitMode.Never,
        AllowVtClipboardWrite = false, AllowOscNotifications = false
    };

    public void SetZoom(double percent)
    {
        var size = settings.FontSize * 4 / 3 * percent / 100;
        Control.AdjustFontSize(size - Control.FontSize);
    }

    public async Task CloseAsync()
    {
        source.OutputAppended -= Append;
        source.Cleared -= Clear;
        await Control.CloseAsync();
    }

    private void Clear()
    {
        Control.ResetTerminal();
        Control.Engine.Feed("\x1b[?25l");
        lastKind = null;
        lastStyle = null;
        lastWasCarriageReturn = false;
    }

    private void Append(IReadOnlyList<OutputEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Kind == OutputKind.Command)
            {
                Control.Engine.Feed("\x1b[0m");
                var start = Math.Clamp(entry.CodeStart, 0, entry.Text.Length);
                Feed(entry.Text[..start]);
                var code = entry.Text[start..];
                var offset = 0;
                Token? previous = null;
                foreach (var token in EditorAnalysis.Analyze(code).Tokens)
                {
                    var begin = token.Extent.StartOffset;
                    var end = token.Extent.EndOffset;
                    if (begin < offset || end <= begin || end > code.Length) continue;
                    Feed(code[offset..begin]);
                    var brush = DesktopTheme.HighContrast ? null : PowerShellColorizer.ColorFor(token, previous, settings.Theme, "Console");
                    previous = token;
                    if (brush is SolidColorBrush color) Control.Engine.Feed(ColorSequence(color.Color, true));
                    Feed(code[begin..end]);
                    Control.Engine.Feed("\x1b[39m");
                    offset = end;
                }
                Feed(code[offset..]);
                lastKind = null;
                lastStyle = null;
            }
            else
            {
                if (lastKind != entry.Kind || lastStyle != entry.Style)
                {
                    var key = entry.Kind switch
                    {
                        OutputKind.Error => "Stream.Error", OutputKind.Warning => "Stream.Warning",
                        OutputKind.Verbose => "Stream.Verbose", OutputKind.Debug => "Stream.Debug",
                        _ => "Console.Foreground"
                    };
                    Control.Engine.Feed("\x1b[0m");
                    if (!DesktopTheme.HighContrast)
                    {
                        Control.Engine.Feed(ColorSequence(Color.Parse(entry.Style?.Foreground ?? settings.Theme.Colors[key]), true));
                        if (entry.Style?.Background is { } background) Control.Engine.Feed(ColorSequence(Color.Parse(background), false));
                        if (entry.Style?.Bold == true) Control.Engine.Feed("\x1b[1m");
                        if (entry.Style?.Underline == true) Control.Engine.Feed("\x1b[4m");
                        if (entry.Style?.Inverse == true) Control.Engine.Feed("\x1b[7m");
                    }
                    lastKind = entry.Kind;
                    lastStyle = entry.Style;
                }
                Feed(entry.Text);
            }
        }
    }

    private void Feed(string text)
    {
        var normalized = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character == '\n' && !lastWasCarriageReturn) normalized.Append('\r');
            normalized.Append(character);
            lastWasCarriageReturn = character == '\r';
        }
        Control.Engine.Feed(normalized.ToString());
    }

    private static string ColorSequence(Color color, bool foreground) =>
        $"\x1b[{(foreground ? 38 : 48)};2;{color.R};{color.G};{color.B}m";

    // A VT output surface has no process or raw input queue. Keyboard editing belongs to ConsoleEditor.
    private sealed class OutputOnlyConnection : IRestartableTerminalConnection
    {
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }
        public event EventHandler<Exception>? Faulted { add { } remove { } }
        public event EventHandler<TerminalExitInfo>? SessionExited { add { } remove { } }
        public bool IsRunning => State == TerminalConnectionState.Connected;
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public TerminalConnectionCapabilities Capabilities => TerminalConnectionCapabilities.Resize;
        public TerminalConnectionState State { get; private set; }
        public TerminalProcessMetadata? ProcessMetadata => null;
        public TerminalExitInfo? LastExitInfo => null;
        public Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Resize(options.Columns, options.Rows);
            State = TerminalConnectionState.Connected;
            return Task.CompletedTask;
        }
        public Task StartAsync(string commandLine, string? workingDirectory, int columns, int rows, CancellationToken cancellationToken = default) =>
            StartAsync(new TerminalLaunchOptions { CommandLine = commandLine, Columns = columns, Rows = rows }, cancellationToken);
        public void Resize(int columns, int rows) { Columns = columns; Rows = rows; }
        public void Write(ReadOnlySpan<byte> data) { }
        public void Write(string text) { }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public Task RestartAsync(TerminalLaunchOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The PowerShell output surface cannot restart a process.");
        public Task CloseAsync(CancellationToken cancellationToken = default)
        { State = TerminalConnectionState.Closed; return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        { State = TerminalConnectionState.Disposed; return ValueTask.CompletedTask; }
    }
}
