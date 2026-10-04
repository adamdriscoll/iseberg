using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Iseberg.Core;

namespace Iseberg;

public sealed class SnippetWindow : Window
{
    public SnippetWindow(IReadOnlyList<PowerShellSnippet> snippets)
    {
        Title = UiText.Get("Snippets").Replace("_", "").TrimEnd('.');
        Width = 700;
        Height = 520;
        MinWidth = 500;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var search = new TextBox { Name = "SnippetSearch", Watermark = UiText.Get("SearchSnippets") };
        var list = new ListBox { Name = "SnippetList" };
        var description = new TextBlock { Name = "SnippetDescription", TextWrapping = TextWrapping.Wrap };
        var preview = new TextBox { Name = "SnippetPreview", IsReadOnly = true, AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Menlo, monospace") };
        var insert = new Button { Name = "InsertSnippet", Content = UiText.Get("Insert"), IsDefault = true };
        var cancel = new Button { Content = UiText.Get("Cancel"), IsCancel = true };
        AutomationProperties.SetName(search, UiText.Get("SearchSnippets"));
        AutomationProperties.SetName(list, UiText.Get("Snippets").Replace("_", "").TrimEnd('.'));
        AutomationProperties.SetName(preview, UiText.Get("SnippetPreview"));
        void Select()
        {
            var snippet = list.SelectedItem as PowerShellSnippet;
            preview.Text = snippet?.Code ?? "";
            description.Text = snippet is null ? "" : string.Join("\n", new[] { snippet.Description, snippet.Author, snippet.Compatibility }.Where(s => s.Length > 0));
            insert.IsEnabled = snippet is not null;
        }
        void Filter()
        {
            var text = search.Text ?? "";
            list.ItemsSource = snippets.Where(s => s.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(text, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToArray();
            list.SelectedIndex = list.ItemCount > 0 ? 0 : -1;
            Select();
        }
        search.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) => Select();
        insert.Click += (_, _) => Close(list.SelectedItem as PowerShellSnippet);
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is PowerShellSnippet snippet) Close(snippet); };
        cancel.Click += (_, _) => Close(null);
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto,2*,Auto"), Margin = new Thickness(12), RowSpacing = 8 };
        grid.Children.Add(search);
        Grid.SetRow(list, 1); grid.Children.Add(list);
        Grid.SetRow(description, 2); grid.Children.Add(description);
        Grid.SetRow(preview, 3); grid.Children.Add(preview);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(insert); buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 4); grid.Children.Add(buttons);
        Content = grid;
        Dialogs.RegisterNames(this);
        Filter();
        Opened += (_, _) => search.Focus();
    }
}
