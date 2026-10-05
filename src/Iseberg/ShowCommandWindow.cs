using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Iseberg.Core;

namespace Iseberg;

public sealed record ShowCommandResult(string Script, bool Run);

public sealed class ShowCommandWindow : Window
{
    public ShowCommandWindow(CommandForm form, bool canInsert, Func<Window, Task>? showHelp = null, bool passThru = false)
    {
        ClassicDialog.Apply(this);
        Title = form.Description.Name;
        Width = 360 * DesktopTheme.TextScale;
        Height = 410 * DesktopTheme.TextScale;
        MinWidth = 300;
        MinHeight = 300;
        var view = new CommandFormView { Name = "ShowCommandForm", Compact = true };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var run = new Button { Name = "ShowCommandRun", Content = UiText.Get(passThru ? "OK" : "RunButton"), IsDefault = true };
        var insert = new MenuItem { Name = "ShowCommandInsert", Header = UiText.Get("Insert") };
        var copy = new Button { Name = "ShowCommandCopy", Content = UiText.Get("CopyCommand") };
        var help = new Button { Name = "ShowCommandHelp", Content = new ToolbarIcon { Kind = "Question", Width = 16, Height = 16 },
            IsEnabled = showHelp is not null, Width = 23, Height = 23 };
        AutomationProperties.SetName(help, UiText.Get("HelpButton"));
        ToolTip.SetTip(help, UiText.Get("HelpButton"));
        var cancel = new Button { Content = UiText.Get("Cancel"), IsCancel = true };
        view.CommandChanged += () =>
        {
            run.IsEnabled = copy.IsEnabled = view.Result is { IsValid: true };
            insert.IsEnabled = canInsert && run.IsEnabled;
        };
        run.Click += (_, _) => Close(new ShowCommandResult(view.GetCommand(), true));
        insert.Click += (_, _) => Close(new ShowCommandResult(view.GetCommand(), false));
        copy.Click += async (_, _) =>
        {
            try
            {
                var clipboard = Clipboard ?? throw new InvalidOperationException(UiText.Get("ClipboardUnavailable"));
                await clipboard.SetTextAsync(view.GetCommand());
                error.Text = UiText.Get("CommandCopied");
                error.IsVisible = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException)
            {
                System.Diagnostics.Trace.TraceError("Command copy failed: {0}", exception);
                error.Text = exception.Message;
                error.IsVisible = true;
            }
        };
        cancel.Click += (_, _) => Close(null);
        help.Click += async (_, _) =>
        {
            if (showHelp is null) return;
            try { await showHelp(this); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Management.Automation.RuntimeException or NotSupportedException)
            {
                System.Diagnostics.Trace.TraceError("Command help failed: {0}", exception);
                error.Text = exception.Message;
                error.IsVisible = true;
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 5 };
        buttons.Children.Add(run); buttons.Children.Add(copy); buttons.Children.Add(cancel);
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(new TextBlock { Text = string.Format(UiText.Get("CommandParametersTitle"), form.Description.Name),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(help, 1); header.Children.Add(help);
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new Thickness(6), RowSpacing = 5 };
        grid.Children.Add(header);
        Grid.SetRow(view, 1); grid.Children.Add(view);
        Grid.SetRow(error, 2); grid.Children.Add(error);
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        ContextMenu = new ContextMenu { Items = { insert } };
        Content = grid;
        Dialogs.RegisterNames(this);
        var scope = NameScope.GetNameScope(this)!;
        if (scope.Find(insert.Name!) is null) scope.Register(insert.Name!, insert);
        view.ShowCommand(form);
        ToolTip.SetTip(run, view.FindControl<TextBlock>("CommandValidation")!.Text);
        view.CommandChanged += () => ToolTip.SetTip(run, view.FindControl<TextBlock>("CommandValidation")!.Text);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var tabs = view.FindControl<Avalonia.Controls.Primitives.TabStrip>("ParameterSetTabs")!;
                tabs.SelectedIndex = (tabs.SelectedIndex + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? tabs.ItemCount - 1 : 1)) % tabs.ItemCount;
                e.Handled = true;
            }
            else if (e.Key == Key.F1 && help.IsEnabled)
            {
                help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
