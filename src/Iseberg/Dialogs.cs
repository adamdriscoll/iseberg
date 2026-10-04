using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.LogicalTree;

namespace Iseberg;

public static class Dialogs
{
    internal static void RegisterNames(Window window)
    {
        var scope = new NameScope();
        NameScope.SetNameScope(window, scope);
        foreach (var control in window.GetLogicalDescendants().OfType<Control>())
            if (control.Name is { Length: > 0 } name) scope.Register(name, control);
    }

    public static async Task<string?> AskAsync(Window owner, string title, string message, string initial = "", bool secret = false)
    {
        var input = new TextBox { Text = initial, PasswordChar = secret ? '*' : '\0', MinWidth = 330 };
        var window = Create(title, 460);
        var panel = Body(message);
        panel.Children.Add(input);
        var buttons = Buttons();
        var ok = new Button { Content = UiText.Get("OK"), IsDefault = true, MinWidth = 75 };
        var cancel = new Button { Content = UiText.Get("Cancel"), IsCancel = true, MinWidth = 75 };
        ok.Click += (_, _) => window.Close(input.Text ?? "");
        cancel.Click += (_, _) => window.Close(null);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task<string?> ChooseAsync(Window owner, string title, string message, params string[] choices)
    {
        var window = Create(title, 460);
        var panel = Body(message);
        var buttons = Buttons();
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice, MinWidth = 75, IsCancel = choice == UiText.Get("Cancel") };
            button.Click += (_, _) => window.Close(choice);
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        window.Content = panel;
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task ShowTextAsync(Window owner, string title, string text)
    {
        var window = Create(title, 760);
        window.Height = 560;
        window.CanResize = true;
        window.Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Menlo, monospace"),
            Margin = new Thickness(10)
        };
        await window.ShowDialog(owner);
    }

    private static Window Create(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false
    };
    private static StackPanel Body(string message)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        return panel;
    }
    private static StackPanel Buttons() => new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
}
