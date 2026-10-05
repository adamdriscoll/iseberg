using System.Management.Automation;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Iseberg.Core;

namespace Iseberg;

public sealed class BreakpointWindow : Window
{
    private readonly ComboBox kind = new() { Name = "BreakpointKind", ItemsSource = Enum.GetValues<BreakpointKind>() };
    private readonly TextBox script = new() { Name = "BreakpointScript" };
    private readonly TextBox target = new() { Name = "BreakpointTarget" };
    private readonly TextBox line = new() { Name = "BreakpointLine" };
    private readonly TextBox condition = new() { Name = "BreakpointCondition" };
    private readonly TextBox action = new() { Name = "BreakpointAction" };
    private readonly ComboBox access = new() { Name = "BreakpointAccess", ItemsSource = Enum.GetValues<VariableAccessMode>() };
    private readonly CheckBox enabled = new() { Name = "BreakpointEnabled", Content = UiText.Get("BreakpointEnabled") };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };

    public BreakpointWindow(BreakpointSpec spec)
    {
        Title = UiText.Get("BreakpointsTitle");
        Width = 570;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ClassicDialog.Apply(this);
        var body = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        void Field(string key, Control control)
        {
            body.Children.Add(new TextBlock { Text = UiText.Get(key) });
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(control, UiText.Get(key));
            body.Children.Add(control);
        }
        Field("BreakpointKind", kind);
        Field("BreakpointScript", script);
        Field("BreakpointTarget", target);
        Field("BreakpointLine", line);
        Field("BreakpointAccess", access);
        Field("BreakpointCondition", condition);
        Field("BreakpointAction", action);
        body.Children.Add(enabled);
        body.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var ok = new Button { Name = "BreakpointOK", Content = UiText.Get("OK"), IsDefault = true };
        var cancel = new Button { Content = UiText.Get("Cancel"), IsCancel = true };
        ok.Click += (_, _) =>
        {
            try
            {
                var value = ReadSpec();
                value.Validate();
                Close(value);
            }
            catch (Exception exception) when (exception is ArgumentException or RuntimeException)
            {
                error.Text = exception.Message;
            }
        };
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);
        Content = body;
        kind.SelectionChanged += (_, _) =>
        {
            line.IsEnabled = kind.SelectedItem is BreakpointKind.Line;
            target.IsEnabled = kind.SelectedItem is BreakpointKind.Command or BreakpointKind.Variable;
            access.IsEnabled = kind.SelectedItem is BreakpointKind.Variable;
        };
        script.Text = spec.ScriptPath ?? "";
        target.Text = spec.Target;
        line.Text = (spec.Line > 0 ? spec.Line : 1).ToString();
        condition.Text = spec.Condition;
        action.Text = spec.Action;
        enabled.IsChecked = spec.Enabled;
        access.SelectedItem = spec.AccessMode;
        kind.SelectedItem = spec.Kind;
        Dialogs.RegisterNames(this);
    }

    public BreakpointSpec ReadSpec()
    {
        var breakpointKind = kind.SelectedItem is BreakpointKind value ? value : throw new ArgumentException("Select a breakpoint kind.");
        if (breakpointKind == BreakpointKind.Line && (!int.TryParse(line.Text, out var number) || number < 1))
            throw new ArgumentException("Enter a positive line number.");
        return new(breakpointKind, string.IsNullOrWhiteSpace(script.Text) ? null : Path.GetFullPath(script.Text.Trim()),
            target.Text?.Trim() ?? "", breakpointKind == BreakpointKind.Line ? int.Parse(line.Text!) : 0,
            condition.Text ?? "", enabled.IsChecked == true,
            access.SelectedItem is VariableAccessMode mode ? mode : VariableAccessMode.Write, action.Text ?? "");
    }
}
