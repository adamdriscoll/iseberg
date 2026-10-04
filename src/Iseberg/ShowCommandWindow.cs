using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Iseberg.Core;

namespace Iseberg;

public sealed record ShowCommandResult(string Script, bool Run);

public sealed class ShowCommandWindow : Window
{
    public ShowCommandWindow(CommandForm form, bool canInsert, Func<Window, Task>? showHelp = null)
    {
        Title = UiText.Get("ShowCommand").Replace("_", "").TrimEnd('.');
        Width = 620;
        Height = 700;
        MinWidth = 400;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var view = new CommandFormView { Name = "ShowCommandForm" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var run = new Button { Name = "ShowCommandRun", Content = UiText.Get("RunButton"), IsDefault = true };
        var insert = new Button { Name = "ShowCommandInsert", Content = UiText.Get("Insert") };
        var copy = new Button { Name = "ShowCommandCopy", Content = UiText.Get("CopyCommand") };
        var help = new Button { Content = UiText.Get("HelpButton"), IsEnabled = showHelp is not null };
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
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException)
            {
                System.Diagnostics.Trace.TraceError("Command copy failed: {0}", exception);
                error.Text = exception.Message;
            }
        };
        cancel.Click += (_, _) => Close(null);
        help.Click += async (_, _) =>
        {
            if (showHelp is null) return;
            try { await showHelp(this); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Management.Automation.RuntimeException)
            {
                System.Diagnostics.Trace.TraceError("Command help failed: {0}", exception);
                error.Text = exception.Message;
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(help); buttons.Children.Add(copy); buttons.Children.Add(insert); buttons.Children.Add(run); buttons.Children.Add(cancel);
        var grid = new Grid { RowDefinitions = new("*,Auto,Auto"), Margin = new Thickness(12), RowSpacing = 8 };
        grid.Children.Add(view);
        Grid.SetRow(error, 1); grid.Children.Add(error);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        Content = grid;
        Dialogs.RegisterNames(this);
        view.ShowCommand(form);
    }
}
