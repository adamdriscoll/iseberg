using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class TerminalApplicationTests
{
    [Fact]
    public void ArgumentQuotingPreservesMetacharactersAndTrailingSlashes()
    {
        Assert.Equal("\"a b\\\\\"", TerminalApplication.QuoteWindowsArgument("a b\\"));
        Assert.Equal("\"a\\\"b\"", TerminalApplication.QuoteWindowsArgument("a\"b"));
        Assert.Equal("\"\"", TerminalApplication.QuoteWindowsArgument(""));
        Assert.Equal("'a'\"'\"'b; $(touch injected)'", TerminalApplication.QuoteUnixArgument("a'b; $(touch injected)"));
    }

    [Fact]
    public async Task MissingExecutableIsReportedWithoutStartingATerminal()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => TerminalApplication.RunAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), [], Path.GetTempPath()));
        await using var session = new PowerShellSession();
        var output = new ConcurrentQueue<OutputEntry>();
        session.Output += output.Enqueue;
        await session.InitializeAsync();
        await session.ExecuteAsync("Start-IsebergTerminal no-such-iseberg-native-program");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Error && entry.Text.Contains("was not found"));
        Assert.Equal(SessionState.Ready, session.State);
    }

    [Fact]
    public async Task WindowsTerminalUsesRealConsoleInputOutputAndPreservesArgumentsAndExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = FindPowerShell();
        var resultPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var script = """
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            public static class TerminalInput {
                [StructLayout(LayoutKind.Explicit, CharSet=CharSet.Unicode, Size=20)]
                public struct Record {
                    [FieldOffset(0)] public short Type;
                    [FieldOffset(4)] public int Down;
                    [FieldOffset(8)] public short Repeat;
                    [FieldOffset(10)] public short Key;
                    [FieldOffset(14)] public char Character;
                }
                [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n);
                [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
                public static extern bool WriteConsoleInputW(IntPtr h, Record[] r, int count, out int written);
                public static void Send(string text) {
                    var records = new Record[text.Length];
                    for (int i=0; i<text.Length; i++) records[i] = new Record {
                        Type=1, Down=1, Repeat=1, Key=(short)(text[i]=='\r' ? 13 : 0), Character=text[i]
                    };
                    int written;
                    if (!WriteConsoleInputW(GetStdHandle(-10), records, records.Length, out written) || written!=records.Length)
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            '@
            [TerminalInput]::Send("native-input`r")
            $value = [Console]::ReadLine()
            [Console]::WriteLine("native-output:$value")
            [IO.File]::WriteAllText($resultPath, "input=$value;output=$([Console]::IsOutputRedirected);cwd=$((Get-Location).Path)")
            exit 7
            """;
        try
        {
            await using var session = new PowerShellSession();
            var output = new ConcurrentQueue<OutputEntry>();
            session.Output += output.Enqueue;
            await session.InitializeAsync();
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes($"$resultPath = '{resultPath.Replace("'", "''")}'; " + script));
            await session.ExecuteAsync($"$exitCode = Start-IsebergTerminal '{executable.Replace("'", "''")}' " +
                $"-ArgumentList @('-NoProfile', '-EncodedCommand', '{encoded}') -WorkingDirectory '{Path.GetTempPath().Replace("'", "''")}'; " +
                "\"exit=$exitCode;last=$LASTEXITCODE\"").WaitAsync(TimeSpan.FromSeconds(30));
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
            Assert.Equal($"input=native-input;output=False;cwd={Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)}",
                await File.ReadAllTextAsync(resultPath));
            Assert.Contains(output, entry => entry.Kind == OutputKind.Output && entry.Text.Contains("exit=7;last=7"));
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        }
        finally
        {
            File.Delete(resultPath);
        }
    }

    [Fact]
    public async Task WindowsTerminalStopKillsOnlyTheLaunchedApplicationAndReleasesGate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = FindPowerShell();
        var pidPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pid");
        try
        {
            await using var session = new PowerShellSession();
            await session.InitializeAsync();
            var script = $"[IO.File]::WriteAllText('{pidPath.Replace("'", "''")}', [string]$PID); Start-Sleep -Seconds 60";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var execution = session.ExecuteAsync($"Start-IsebergTerminal '{executable.Replace("'", "''")}' " +
                $"-ArgumentList @('-NoProfile', '-EncodedCommand', '{encoded}')");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(pidPath))
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Native terminal did not start.");
                await Task.Delay(30);
            }
            var pid = int.Parse(await File.ReadAllTextAsync(pidPath));
            using var process = Process.GetProcessById(pid);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync("'overlap'"));
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);
            Assert.Equal(SessionState.Ready, session.State);
            await session.ExecuteAsync("'after-terminal'");
        }
        finally { File.Delete(pidPath); }
    }

    private static string FindPowerShell() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "pwsh.exe")).First(File.Exists);
}
