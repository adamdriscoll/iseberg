using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Iseberg.Core;

namespace Iseberg;

public sealed class HelpSettingsWindow : Window
{
    public HelpSettingsWindow(HelpViewSettings settings)
    {
        ClassicDialog.Apply(this);
        Title = UiText.Get("HelpSettings");
        Width = 220 * DesktopTheme.TextScale;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        var draft = settings.Copy();
        var sections = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto"), RowSpacing = 9 };
        var kinds = Enum.GetValues<HelpSectionKind>();
        foreach (var (kind, index) in kinds.Select((kind, index) => (kind, index)))
        {
            var check = new CheckBox { Name = "HelpSection" + kind, Content = UiText.Get("Help" + kind),
                IsChecked = draft.Sections.Contains(kind) };
            check.IsCheckedChanged += (_, _) =>
            {
                draft.Sections.Remove(kind);
                if (check.IsChecked == true) draft.Sections.Add(kind);
            };
            Grid.SetColumn(check, index / 5); Grid.SetRow(check, index % 5); sections.Children.Add(check);
        }
        var search = new StackPanel { Spacing = 9 };
        var matchCase = new CheckBox { Name = "HelpMatchCase", Content = UiText.Get("HelpMatchCase"), IsChecked = draft.MatchCase };
        var wholeWord = new CheckBox { Name = "HelpWholeWord", Content = UiText.Get("HelpWholeWord"), IsChecked = draft.WholeWord };
        matchCase.IsCheckedChanged += (_, _) => draft.MatchCase = matchCase.IsChecked == true;
        wholeWord.IsCheckedChanged += (_, _) => draft.WholeWord = wholeWord.IsChecked == true;
        search.Children.Add(matchCase); search.Children.Add(wholeWord);
        var ok = new Button { Name = "AcceptHelpSettings", Content = UiText.Get("OK"), IsDefault = true, MinWidth = 45 };
        var cancel = new Button { Name = "CancelHelpSettings", Content = UiText.Get("Cancel"), IsCancel = true, MinWidth = 45 };
        ok.Click += (_, _) => Close(draft.Copy());
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 5 };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(6), Spacing = 12 };
        panel.Children.Add(Group(UiText.Get("HelpSections"), sections));
        panel.Children.Add(Group(UiText.Get("HelpSearchOptions"), search));
        panel.Children.Add(buttons);
        Content = panel;
        Dialogs.RegisterNames(this);
    }

    private static Control Group(string title, Control content)
    {
        var grid = new Grid { RowDefinitions = new("Auto,*") };
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            BorderBrush = DesktopTheme.Brush("BorderBrush"), Padding = new Thickness(14,16,8,14),
            Margin = new Thickness(0,7,0,0), Child = content };
        Grid.SetRowSpan(frame, 2); grid.Children.Add(frame);
        var label = new TextBlock { Text = title, Background = DesktopTheme.Brush("WindowBrush"),
            Margin = new Thickness(6,0,0,0), Padding = new Thickness(2,0), HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(label);
        AutomationProperties.SetName(grid, title);
        return grid;
    }
}
