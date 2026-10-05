using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Media;
using Iseberg.Core;

namespace Iseberg;

public sealed class HostInputWindow : Window
{
    public HostInputWindow(InputRequest request)
    {
        Title = request.Caption;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var body = new StackPanel { Margin = new(16), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = request.Message, TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Name = "HostInput", PasswordChar = request.Secret ? '*' : '\0' };
        AutomationProperties.SetName(input, request.Message);
        var choices = new List<ToggleButton>();
        if (request.Choices.Count == 0) body.Children.Add(input);
        else
        {
            var list = new StackPanel { Spacing = 6 };
            for (var i = 0; i < request.Choices.Count; i++)
            {
                ToggleButton choice = request.MultipleChoice ? new CheckBox() : new RadioButton { GroupName = "HostChoice" };
                choice.Name = "HostChoice" + i;
                choice.Content = request.Choices[i].Label;
                choice.IsChecked = request.DefaultChoices.Contains(i);
                ToolTip.SetTip(choice, request.Choices[i].Help);
                choices.Add(choice);
                list.Children.Add(choice);
            }
            body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360 });
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var ok = new Button { Name = "HostInputSubmit", Content = UiText.Get("OK"), IsDefault = true };
        var cancel = new Button { Name = "HostInputCancel", Content = UiText.Get("Cancel"), IsCancel = true };
        void Update() => ok.IsEnabled = choices.Count == 0 || request.MultipleChoice || choices.Any(choice => choice.IsChecked == true);
        foreach (var choice in choices) choice.IsCheckedChanged += (_, _) => Update();
        Update();
        ok.Click += (_, _) => Close(choices.Count == 0 ? input.Text ?? "" :
            choices.Any(choice => choice.IsChecked == true)
                ? string.Join(",", choices.Select((choice, i) => (choice, i)).Where(pair => pair.choice.IsChecked == true).Select(pair => pair.i))
                : "-");
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);
        Content = body;
        Dialogs.RegisterNames(this);
        Opened += (_, _) =>
        {
            if (choices.Count == 0) input.Focus();
            else (choices.FirstOrDefault(choice => choice.IsChecked == true) ?? choices[0]).Focus();
        };
    }
}
