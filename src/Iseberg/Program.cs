using Avalonia;
using System.Runtime.CompilerServices;
#if DEBUG
using Avalonia.Diagnostics;
#endif

namespace Iseberg;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var checkRuntime = args is ["--check-runtime"];
        try
        {
#if ISEBERG_COMPACT
            PowerShellInstallation.Find().Register();
#endif
            if (checkRuntime)
            {
                CheckRuntime();
                return 0;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.IO.IOException or
            UnauthorizedAccessException or System.Reflection.ReflectionTypeLoadException)
        {
            Console.Error.WriteLine(exception);
            if (checkRuntime) return 1;
            StartupFailureApp.Message = exception.Message;
            AppBuilder.Configure<StartupFailureApp>().UsePlatformDetect().LogToTrace()
                .StartWithClassicDesktopLifetime([]);
            return 1;
        }
        return StartDesktop(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int StartDesktop(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckRuntime() => RuntimeCheck.RunAsync().GetAwaiter().GetResult();

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
#if DEBUG
        builder.WithDeveloperTools();
#endif
        return builder;
    }
}
