using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Iseberg.Core;

namespace Iseberg;

public sealed class ShowCommandPickerWindow : Window
{
    public ShowCommandPickerWindow(IReadOnlyList<CommandDescription> commands)
    {
        Title = UiText.Get("ShowCommand").Replace("_", "").TrimEnd('.');
        Width = 500;
        Height = 600;
        MinWidth = 350;
        MinHeight = 350;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var search = new TextBox { Name = "ShowCommandSearch", Watermark = UiText.Get("Name") };
        var modules = new ComboBox
        {
            ItemsSource = new[] { UiText.Get("AllModules") }.Concat(commands.Select(command => command.Module)
                .Where(module => module.Length > 0).Distinct().Order()).ToArray(),
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var list = new ListBox
        {
            Name = "ShowCommandList",
            ItemTemplate = new FuncDataTemplate<CommandDescription>((command, _) => new TextBlock { Text = command?.Name })
        };
        AutomationProperties.SetName(search, UiText.Get("CommandSearch"));
        AutomationProperties.SetName(modules, UiText.Get("Modules"));
        AutomationProperties.SetName(list, UiText.Get("CommandsTitle"));
        var select = new Button { Name = "ShowCommandSelect", Content = UiText.Get("OK"), IsDefault = true, IsEnabled = false };
        var cancel = new Button { Content = UiText.Get("Cancel"), IsCancel = true };
        void Filter()
        {
            list.ItemsSource = commands.Where(command =>
                command.Name.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase) &&
                (modules.SelectedIndex == 0 || command.Module == modules.SelectedItem as string)).ToArray();
            list.SelectedIndex = list.ItemCount > 0 ? 0 : -1;
        }
        void Select()
        {
            if (list.SelectedItem is CommandDescription command)
                Close(string.IsNullOrEmpty(command.Module) ? command.Name : command.Module + "\\" + command.Name);
        }
        search.TextChanged += (_, _) => Filter();
        modules.SelectionChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) => select.IsEnabled = list.SelectedItem is CommandDescription;
        select.Click += (_, _) => Select();
        list.DoubleTapped += (_, _) => Select();
        cancel.Click += (_, _) => Close(null);
        var grid = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(12), RowSpacing = 8 };
        grid.Children.Add(modules);
        Grid.SetRow(search, 1); grid.Children.Add(search);
        Grid.SetRow(list, 2); grid.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(select); buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        Content = grid;
        Dialogs.RegisterNames(this);
        Filter();
        Opened += (_, _) => search.Focus();
    }
}
