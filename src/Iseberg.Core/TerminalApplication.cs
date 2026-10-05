using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Iseberg.Core;

public static class TerminalApplication
{
    public static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The terminal application must resolve to an existing executable.", executable);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);
        if (arguments.Any(argument => argument is null || argument.Contains('\0')))
            throw new ArgumentException("Terminal arguments cannot be null or contain NUL.", nameof(arguments));
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            using var process = StartWindows(executable, arguments, workingDirectory);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            return process.ExitCode;
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("System terminal integration supports Windows, Linux (xterm) and macOS (Terminal.app).");
        return await RunUnixAsync(executable, arguments, workingDirectory, cancellationToken);
    }

    public static string QuoteWindowsArgument(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static string QuoteUnixArgument(string argument) => "'" + argument.Replace("'", "'\"'\"'") + "'";

    private static Process StartWindows(string executable, IReadOnlyList<string> arguments, string directory)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteWindowsArgument)));
        if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00000010,
            IntPtr.Zero, directory, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the application in a new Windows console.");
        try
        {
            var process = Process.GetProcessById(info.ProcessId);
            _ = process.SafeHandle;
            return process;
        }
        finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
    }

    private static async Task<int> RunUnixAsync(string executable, IReadOnlyList<string> arguments, string directory,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var folder = Directory.CreateTempSubdirectory("iseberg-terminal-");
        var script = Path.Combine(folder.FullName, "run.command");
        var pidPath = Path.Combine(folder.FullName, "pid");
        var exitPath = Path.Combine(folder.FullName, "exit");
        var goPath = Path.Combine(folder.FullName, "go");
        Process? child = null;
        Process? terminal = null;
        try
        {
            var command = string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteUnixArgument));
            await File.WriteAllTextAsync(script, $$"""
                #!/bin/sh
                printf '%s' "$$" > {{QuoteUnixArgument(pidPath + ".tmp")}}
                mv {{QuoteUnixArgument(pidPath + ".tmp")}} {{QuoteUnixArgument(pidPath)}}
                child=''
                finish() {
                    code=$?
                    trap - EXIT HUP INT TERM
                    if [ -n "$child" ]; then kill -TERM "$child" 2>/dev/null; fi
                    printf '%s' "$code" > {{QuoteUnixArgument(exitPath + ".tmp")}}
                    mv {{QuoteUnixArgument(exitPath + ".tmp")}} {{QuoteUnixArgument(exitPath)}}
                }
                trap finish EXIT
                trap 'exit 130' HUP INT TERM
                while [ ! -f {{QuoteUnixArgument(goPath)}} ]; do
                    [ -f {{QuoteUnixArgument(script)}} ] || exit 130
                    sleep 0.05
                done
                cd {{QuoteUnixArgument(directory)}} || exit 125
                {{command}} < /dev/tty > /dev/tty 2>&1 &
                child=$!
                wait "$child"
                code=$?
                child=''
                exit "$code"
                """ + "\n", cancellationToken);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xterm",
                UseShellExecute = false
            };
            if (OperatingSystem.IsMacOS())
            {
                start.ArgumentList.Add("-a");
                start.ArgumentList.Add("Terminal");
                start.ArgumentList.Add(script);
            }
            else
            {
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add("/bin/sh");
                start.ArgumentList.Add(script);
            }
            try { terminal = Process.Start(start) ?? throw new InvalidOperationException("The system terminal did not start."); }
            catch (Win32Exception exception)
            { throw new InvalidOperationException("Could not open a system terminal. Linux requires xterm and a graphical display; macOS requires Terminal.app.", exception); }
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(exitPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child is null && File.Exists(pidPath))
                {
                    var pid = await File.ReadAllTextAsync(pidPath, cancellationToken);
                    if (int.TryParse(pid, out var id))
                    {
                        try
                        {
                            child = Process.GetProcessById(id);
                            cancellationToken.ThrowIfCancellationRequested();
                            await File.WriteAllTextAsync(goPath, "", cancellationToken);
                        }
                        catch (ArgumentException) when (File.Exists(exitPath)) { }
                    }
                }
                if (child?.HasExited == true && !File.Exists(exitPath))
                    throw new InvalidOperationException("The terminal was closed before its application reported an exit status.");
                if (child is null && (deadline.Elapsed > TimeSpan.FromSeconds(20) ||
                    OperatingSystem.IsLinux() && terminal.HasExited))
                    throw new InvalidOperationException("The terminal could not start the application. Check the graphical display and terminal installation.");
                await Task.Delay(50, cancellationToken);
            }
            var status = await File.ReadAllTextAsync(exitPath, cancellationToken);
            if (!int.TryParse(status, out var exitCode))
                throw new InvalidOperationException("The terminal returned an invalid exit status.");
            return exitCode;
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
                child.Dispose();
            }
            if (terminal is not null)
            {
                if (OperatingSystem.IsLinux() && !terminal.HasExited) terminal.Kill(entireProcessTree: true);
                if (OperatingSystem.IsLinux()) await terminal.WaitForExitAsync(CancellationToken.None);
                terminal.Dispose();
            }
            foreach (var path in new[] { script, pidPath, exitPath, goPath, pidPath + ".tmp", exitPath + ".tmp" }) File.Delete(path);
            folder.Delete();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInfo processInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
