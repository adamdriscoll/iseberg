using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace Iseberg;

public sealed record PowerShellInstallation(string Home, string Version, string RuntimeVersion, string Architecture)
{
    public const string HomeVariable = "ISEBERG_PSHOME";
    public static Version MinimumVersion { get; } = new(7, 6, 6);

    public static PowerShellInstallation Find()
    {
        var home = Environment.GetEnvironmentVariable(HomeVariable);
        var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(home) ? executable : Path.Combine(home, executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            """
            [pscustomobject]@{
                Home = $PSHOME
                Version = $PSVersionTable.PSVersion.ToString()
                RuntimeVersion = [Environment]::Version.ToString()
                Architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            } | ConvertTo-Json -Compress
            """
        })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Install PowerShell {MinimumVersion} (7.6.x) on PATH, or set {HomeVariable} to its installation directory.", exception);
        }
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException("PowerShell did not respond to the installation check within 15 seconds.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell installation check failed: {error.GetAwaiter().GetResult().Trim()}");
        PowerShellInstallation installation;
        try
        {
            installation = JsonSerializer.Deserialize<PowerShellInstallation>(output.GetAwaiter().GetResult())
                ?? throw new InvalidOperationException("PowerShell returned an empty installation description.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("PowerShell returned an invalid installation description.", exception);
        }
        installation.Validate(Environment.Version, RuntimeInformation.ProcessArchitecture);
        return installation;
    }

    public void Validate(Version runtimeVersion, Architecture architecture)
    {
        if (!System.Version.TryParse(Version, out var version) ||
            version.Major != MinimumVersion.Major || version.Minor != MinimumVersion.Minor ||
            version < MinimumVersion)
            throw new InvalidOperationException($"Iseberg requires PowerShell {MinimumVersion} or newer in the 7.6.x series; found {Version}.");
        if (!System.Version.TryParse(RuntimeVersion, out var runtime) ||
            runtime.Major != runtimeVersion.Major || runtime.Minor != runtimeVersion.Minor)
            throw new InvalidOperationException($"PowerShell must use .NET {runtimeVersion.Major}.{runtimeVersion.Minor}; found {RuntimeVersion}.");
        if (!string.Equals(Architecture, architecture.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"PowerShell architecture must match Iseberg ({architecture}); found {Architecture}.");
        if (string.IsNullOrWhiteSpace(Home) || !Path.IsPathFullyQualified(Home) ||
            !File.Exists(Path.Combine(Home, "System.Management.Automation.dll")) ||
            !File.Exists(Path.Combine(Home, "pwsh.dll")))
            throw new InvalidOperationException($"PowerShell installation is incomplete: {Home}.");
    }

    public void Register()
    {
        var resolver = new AssemblyDependencyResolver(Path.Combine(Home, "pwsh.dll"));
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = resolver.ResolveAssemblyToPath(name);
            if (path is null && name.Name is { } assemblyName)
            {
                var candidate = Path.Combine(Home, assemblyName + ".dll");
                if (File.Exists(candidate)) path = candidate;
            }
            return path is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        };
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            var path = resolver.ResolveUnmanagedDllToPath(name);
            if (path is null)
            {
                var fileName = OperatingSystem.IsWindows() ? name + ".dll"
                    : OperatingSystem.IsMacOS() ? "lib" + name + ".dylib" : "lib" + name + ".so";
                var candidate = Path.Combine(Home, fileName);
                if (File.Exists(candidate)) path = candidate;
            }
            return path is null ? IntPtr.Zero : NativeLibrary.Load(path);
        };
        // Bind the engine before JIT-compiling any desktop or core code that references its types.
        AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Home, "System.Management.Automation.dll"));
    }
}
