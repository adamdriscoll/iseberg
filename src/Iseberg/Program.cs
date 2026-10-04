using Avalonia;
#if DEBUG
using Avalonia.Diagnostics;
#endif

namespace Iseberg;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
#if DEBUG
        builder.WithDeveloperTools();
#endif
        return builder;
    }
}
