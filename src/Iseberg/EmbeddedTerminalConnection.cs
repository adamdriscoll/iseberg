using Devolutions.Terminal.Connection;
using Iseberg.Core;

namespace Iseberg;

internal sealed class EmbeddedTerminalConnection(TerminalRequest request) : IRestartableTerminalConnection
{
    private readonly IRestartableTerminalConnection connection = CreateConnection();
    private static IRestartableTerminalConnection CreateConnection()
    {
        if (OperatingSystem.IsWindows()) return new ConPtyConnection();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "dt-pty-host")))
                throw new PlatformNotSupportedException("The Devolutions package does not include the Unix PTY host. Use Start-IsebergTerminal -External to open a system terminal.");
            return new LinuxPtyConnection();
        }
        throw new PlatformNotSupportedException("Embedded terminals require Windows, Linux or macOS.");
    }

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived
    { add => connection.OutputReceived += value; remove => connection.OutputReceived -= value; }
    public event EventHandler<int>? Exited
    { add => connection.Exited += value; remove => connection.Exited -= value; }
    public event EventHandler<Exception>? Faulted
    { add => connection.Faulted += value; remove => connection.Faulted -= value; }
    public event EventHandler<TerminalExitInfo>? SessionExited
    { add => connection.SessionExited += value; remove => connection.SessionExited -= value; }
    public bool IsRunning => connection.IsRunning;
    public int Columns => connection.Columns;
    public int Rows => connection.Rows;
    public TerminalConnectionCapabilities Capabilities => connection.Capabilities;
    public TerminalConnectionState State => connection.State;
    public TerminalProcessMetadata? ProcessMetadata => connection.ProcessMetadata;
    public TerminalExitInfo? LastExitInfo => connection.LastExitInfo;

    public Task StartAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
    {
        Func<string, string> quote = OperatingSystem.IsWindows()
            ? TerminalApplication.QuoteWindowsArgument : TerminalApplication.QuoteUnixArgument;
        // Profile expansion must not expand environment-variable references inside literal arguments or paths.
        return connection.StartAsync(options with
        {
            CommandLine = string.Join(" ", new[] { request.Executable }.Concat(request.Arguments).Select(quote)),
            WorkingDirectory = request.WorkingDirectory
        }, cancellationToken);
    }

    public Task StartAsync(string commandLine, string? workingDirectory, int columns, int rows, CancellationToken cancellationToken = default) =>
        StartAsync(new TerminalLaunchOptions { CommandLine = commandLine, WorkingDirectory = workingDirectory, Columns = columns, Rows = rows }, cancellationToken);
    public void Write(ReadOnlySpan<byte> data) => connection.Write(data);
    public void Write(string text) => connection.Write(text);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        connection.WriteAsync(data, cancellationToken);
    public void Resize(int columns, int rows) => connection.Resize(columns, rows);
    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight) => connection.Resize(columns, rows, pixelWidth, pixelHeight);
    public Task RestartAsync(TerminalLaunchOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Run Start-IsebergTerminal again to restart this application.");
    public Task CloseAsync(CancellationToken cancellationToken = default) => connection.CloseAsync(cancellationToken);
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
