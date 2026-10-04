using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Iseberg.Core;

namespace Iseberg;

public sealed class ReplaceWindow : Window
{
    private readonly TextEditor editor;
    private readonly TextBox find = new() { Name = "FindText" };
    private readonly TextBox replacement = new() { Name = "ReplacementText" };
    private readonly CheckBox matchCase = Option("MatchCase");
    private readonly CheckBox wholeWord = Option("WholeWord");
    private readonly CheckBox regex = Option("RegularExpression");
    private readonly CheckBox searchUp = Option("SearchUp");
    private readonly CheckBox wrap = Option("WrapAround", true);
    private readonly CheckBox selection = Option("SelectionOnly");
    private readonly TextBlock status = new() { Name = "ReplaceStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextAnchor selectionStart;
    private readonly TextAnchor selectionEnd;
    private int nextOffset;
    private TextReplacement? current;

    public ReplaceWindow(TextEditor editor)
    {
        this.editor = editor;
        Title = UiText.Get("Replace").Replace("_", "").TrimEnd('.');
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        selectionStart = editor.Document.CreateAnchor(editor.SelectionStart);
        selectionEnd = editor.Document.CreateAnchor(editor.SelectionStart + editor.SelectionLength);
        selectionStart.SurviveDeletion = selectionEnd.SurviveDeletion = true;
        selectionStart.MovementType = AnchorMovementType.BeforeInsertion;
        selectionEnd.MovementType = AnchorMovementType.AfterInsertion;
        selection.IsEnabled = editor.SelectionLength > 0;
        nextOffset = editor.CaretOffset;
        find.Text = editor.SelectedText;
        var body = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        body.Children.Add(new TextBlock { Text = UiText.Get("FindWhat") });
        body.Children.Add(find);
        body.Children.Add(new TextBlock { Text = UiText.Get("ReplaceWith") });
        body.Children.Add(replacement);
        AutomationProperties.SetName(find, UiText.Get("FindWhat"));
        AutomationProperties.SetName(replacement, UiText.Get("ReplaceWith"));
        foreach (var option in new[] { matchCase, wholeWord, regex, searchUp, wrap, selection })
        {
            body.Children.Add(option);
            option.IsCheckedChanged += (_, _) => ResetSearch();
        }
        find.TextChanged += (_, _) => ResetSearch();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        AddButton(buttons, "FindNext", () => Execute(false, false));
        AddButton(buttons, "ReplaceNext", () => Execute(true, false));
        AddButton(buttons, "ReplaceAll", () => Execute(true, true));
        var close = new Button { Content = UiText.Get("Cancel"), IsCancel = true, Margin = new Thickness(3) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        body.Children.Add(buttons);
        body.Children.Add(status);
        AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Polite);
        Content = body;
        Dialogs.RegisterNames(this);
        ResetSearch();
        Opened += (_, _) => { find.Focus(); find.SelectAll(); };
    }

    private void ResetSearch()
    {
        nextOffset = editor.CaretOffset;
        current = editor.SelectionLength > 0 ? new(editor.SelectionStart, editor.SelectionLength, "") : null;
    }

    private void Execute(bool replace, bool all)
    {
        try
        {
            if (editor.IsReadOnly) throw new InvalidOperationException(UiText.Get("ReadOnlyReplace"));
            var options = new ReplacementOptions(matchCase.IsChecked == true, wholeWord.IsChecked == true,
                regex.IsChecked == true, searchUp.IsChecked == true, wrap.IsChecked == true);
            var start = selection.IsChecked == true ? selectionStart.Offset : 0;
            var length = selection.IsChecked == true ? selectionEnd.Offset - start : editor.Document.TextLength;
            var matches = ReplacementSearch.Find(editor.Text, find.Text ?? "", replacement.Text ?? "", options, start, length);
            if (all)
            {
                using (editor.Document.RunUpdate())
                    foreach (var match in matches.Reverse()) editor.Document.Replace(match.Start, match.Length, match.Text);
                status.Text = string.Format(UiText.Get("ReplacementCount"), matches.Count);
                ResetSearch();
                return;
            }
            var matchToUse = replace && current is { } selected
                ? matches.FirstOrDefault(m => m.Start == selected.Start && m.Length == selected.Length)
                : null;
            matchToUse ??= ReplacementSearch.Next(matches, nextOffset, options);
            if (matchToUse is null) { status.Text = UiText.Get("NoMatch"); current = null; return; }
            if (replace)
            {
                editor.Document.Replace(matchToUse.Start, matchToUse.Length, matchToUse.Text);
                editor.Select(matchToUse.Start, matchToUse.Text.Length);
                current = null;
                nextOffset = options.SearchUp ? matchToUse.Start : matchToUse.Start + matchToUse.Text.Length;
                if (!options.SearchUp && matchToUse.Length == 0) nextOffset++;
                status.Text = string.Format(UiText.Get("ReplacementCount"), 1);
            }
            else
            {
                editor.Select(matchToUse.Start, matchToUse.Length);
                current = matchToUse;
                nextOffset = options.SearchUp ? matchToUse.Start : matchToUse.Start + Math.Max(1, matchToUse.Length);
                status.Text = UiText.Get("MatchFound");
            }
            editor.ScrollTo(editor.Document.GetLocation(matchToUse.Start).Line, editor.Document.GetLocation(matchToUse.Start).Column);
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException or InvalidOperationException)
        {
            status.Text = exception.Message;
        }
    }

    private static CheckBox Option(string key, bool value = false) => new() { Name = key, Content = UiText.Get(key), IsChecked = value };
    private static void AddButton(Panel panel, string key, Action action)
    {
        var button = new Button { Name = key, Content = UiText.Get(key), Margin = new Thickness(3) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }
}
