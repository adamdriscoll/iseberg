using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Iseberg;

internal sealed class StartupFailureApp : Application
{
    public static string Message { get; set; } = "";

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            Styles.Add(new Avalonia.Themes.Simple.SimpleTheme());
            var close = new Button { Content = UiText.Get("Close"), HorizontalAlignment = HorizontalAlignment.Right };
            close.Click += (_, _) => desktop.Shutdown(1);
            desktop.MainWindow = new Window
            {
                Title = UiText.Get("StartupFailureTitle"),
                Width = 620,
                Height = 260,
                Content = new StackPanel
                {
                    Margin = new Thickness(24),
                    Spacing = 20,
                    Children =
                    {
                        new TextBox { Text = Message, IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        close
                    }
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
