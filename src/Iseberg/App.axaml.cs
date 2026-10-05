using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Iseberg;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        DesktopTheme.Start();
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(desktop.Args ?? []);
            desktop.MainWindow = window;
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activation)
                activation.Activated += (_, args) =>
                {
                    if (args is FileActivatedEventArgs files)
                        window.OpenActivatedFiles(files.Files.Select(file => file.TryGetLocalPath()
                            ?? throw new InvalidOperationException("An activated file has no local path.")));
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
