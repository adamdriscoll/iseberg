using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace Iseberg;

internal static class ClassicDialog
{
    public static void Apply(Window window)
    {
        window.Classes.Add("options");
        window.Styles.Add(new StyleInclude(new Uri("avares://Iseberg/"))
        {
            Source = new Uri("avares://Iseberg/CommandDialogStyles.axaml")
        });
        window.Icon = AppIcon.Create();
        window.FontSize = 12 * DesktopTheme.TextScale;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.UseLayoutRounding = true;
    }
}
