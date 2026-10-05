using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using Iseberg.Core;

namespace Iseberg;

public sealed record EncodingSelection(ScriptEncoding Encoding, bool Reload);

public sealed class EncodingWindow : Window
{
    public EncodingWindow(ScriptEncoding current, bool canReload, bool openingFile = false)
    {
        Title = UiText.Get("EncodingTitle");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = AppIcon.Create();
        var choices = ScriptEncoding.Choices.ToArray();
        var list = new ComboBox
        {
            Name = "FileEncoding", ItemsSource = choices.Select(choice => choice.DisplayName).ToArray(),
            SelectedIndex = Array.FindIndex(choices, choice => choice.CodePage == current.CodePage && choice.EmitBom == current.EmitBom),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (list.SelectedIndex < 0)
        {
            choices = choices.Append(current).ToArray();
            list.ItemsSource = choices.Select(choice => choice.DisplayName).ToArray();
            list.SelectedIndex = choices.Length - 1;
        }
        AutomationProperties.SetName(list, UiText.Get("EncodingTitle"));
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = UiText.Get(openingFile ? "OpenEncodingHint" : "EncodingHint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(list);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var key in openingFile ? new[] { "OpenWithEncoding", "Cancel" } : new[] { "ConvertEncoding", "ReloadEncoding", "Cancel" })
        {
            var button = new Button
            {
                Name = key, Content = UiText.Get(key), Margin = new Thickness(3),
                IsEnabled = key != "ReloadEncoding" || canReload, IsCancel = key == "Cancel"
            };
            button.Click += (_, _) => Close(key == "Cancel" || list.SelectedIndex < 0 ? null :
                new EncodingSelection(choices[list.SelectedIndex], key == "ReloadEncoding"));
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        Content = panel;
        Dialogs.RegisterNames(this);
    }
}
