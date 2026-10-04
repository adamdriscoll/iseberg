using System.Management.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Iseberg;

public sealed class CompletionPopup : Popup
{
    private readonly ListBox list = new()
    {
        MinWidth = 280, MaxWidth = 560, MaxHeight = 220,
        ItemTemplate = new FuncDataTemplate<CompletionResult>((item, _) =>
            new TextBlock { Text = item?.ListItemText })
    };
    public event EventHandler? ItemInvoked;
    public bool EnterSelects { get; set; } = true;
    public int SelectedIndex { get => list.SelectedIndex; set => list.SelectedIndex = value; }
    public CompletionResult? SelectedItem => list.SelectedItem as CompletionResult;
    public ItemCollection Items => list.Items;
    public IEnumerable<CompletionResult>? Results { set => list.ItemsSource = value; }
    public CompletionPopup()
    {
        IsLightDismissEnabled = true;
        Placement = PlacementMode.Bottom;
        Child = list;
        list.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control source && source.GetVisualAncestors().Prepend(source).Any(v => v is ListBoxItem))
                ItemInvoked?.Invoke(this, EventArgs.Empty);
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab || (e.Key == Key.Enter && EnterSelects))
            {
                ItemInvoked?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.Key is Key.Escape or Key.Enter)
            {
                IsOpen = false;
                PlacementTarget?.Focus();
                e.Handled = true;
            }
        };
    }
}
