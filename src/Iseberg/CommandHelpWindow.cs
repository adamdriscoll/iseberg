using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Iseberg.Core;

namespace Iseberg;

public sealed class CommandHelpWindow : Window
{
    private readonly CommandHelpDocument document;
    private readonly Func<HelpViewSettings, Task> apply;
    private readonly AccessibleTextEditor viewer;
    private readonly TextBox find;
    private readonly Button previous;
    private readonly Button next;
    private readonly TextBlock status;
    private readonly HelpHeadings headings = new();
    private IReadOnlyList<TextReplacement> matches = [];
    private int matchIndex = -1;
    private string baseline;
    private bool closingApproved;
    private bool saving;
    public HelpViewSettings Preferences { get; private set; }

    public CommandHelpWindow(CommandHelpDocument document, HelpViewSettings settings, Func<HelpViewSettings, Task>? apply = null)
    {
        this.document = document;
        this.apply = apply ?? (_ => Task.CompletedTask);
        Preferences = settings.Copy();
        Preferences.Normalize();
        baseline = JsonSerializer.Serialize(Preferences);
        ClassicDialog.Apply(this);
        Title = string.Format(UiText.Get("CommandHelpTitle"), document.Name);
        Width = 580 * DesktopTheme.TextScale;
        Height = 360 * DesktopTheme.TextScale;
        MinWidth = 400;
        MinHeight = 250;
        find = new TextBox { Name = "HelpFind", Width = 150 };
        AutomationProperties.SetName(find, UiText.Get("HelpFind"));
        previous = new Button { Name = "HelpPrevious", Content = UiText.Get("HelpPrevious"), MinWidth = 52, Height = 25 };
        next = new Button { Name = "HelpNext", Content = UiText.Get("HelpNext"), MinWidth = 52, Height = 25 };
        var settingsButton = new Button { Name = "HelpSettingsButton", Content = UiText.Get("HelpSettings"), Height = 25 };
        var header = new Grid { ColumnDefinitions = new("Auto,Auto,Auto,Auto,*,Auto"), ColumnSpacing = 8 };
        header.Children.Add(new TextBlock { Text = UiText.Get("HelpFind") + ":", VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(find, 1); header.Children.Add(find);
        Grid.SetColumn(previous, 2); header.Children.Add(previous);
        Grid.SetColumn(next, 3); header.Children.Add(next);
        Grid.SetColumn(settingsButton, 5); header.Children.Add(settingsButton);
        viewer = new AccessibleTextEditor
        {
            Name = "HelpContent", IsReadOnly = true, WordWrap = true,
            FontFamily = new FontFamily("Lucida Console, Consolas, DejaVu Sans Mono, Menlo, monospace"),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        viewer.Options.AllowScrollBelowDocument = false;
        viewer.Options.HighlightCurrentLine = false;
        viewer.Options.LineHeightFactor = 1;
        viewer.TextArea.TextView.LineTransformers.Add(headings);
        AutomationProperties.SetName(viewer, UiText.Get("HelpContent"));
        status = new TextBlock { Name = "HelpSearchStatus", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var zoomText = new TextBlock { Name = "HelpZoomText", Width = 40, VerticalAlignment = VerticalAlignment.Center };
        var zoom = new Slider { Name = "HelpZoom", Minimum = 20, Maximum = 400, Value = Preferences.Zoom,
            Width = 100, SmallChange = 10, LargeChange = 20 };
        AutomationProperties.SetName(zoom, UiText.Get("HelpZoom"));
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 5 };
        footer.Children.Add(status);
        Grid.SetColumn(zoomText, 1); footer.Children.Add(zoomText);
        Grid.SetColumn(zoom, 2); footer.Children.Add(zoom);
        void SetZoom()
        {
            Preferences.Zoom = zoom.Value;
            viewer.FontSize = 12 * DesktopTheme.TextScale * Preferences.Zoom / 100;
            zoomText.Text = $"{Preferences.Zoom:0}%";
        }
        zoom.ValueChanged += (_, _) => SetZoom();
        SetZoom();
        find.TextChanged += (_, _) => RefreshMatches();
        previous.Click += (_, _) => FindNext(backwards: true);
        next.Click += (_, _) => FindNext(backwards: false);
        find.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                find.Focus(); find.SelectAll(); e.Handled = true;
            }
            else if (e.Key == Key.F3) { FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        settingsButton.Click += async (_, _) =>
        {
            var result = await new HelpSettingsWindow(Preferences).ShowDialog<HelpViewSettings?>(this);
            if (result is null) return;
            try
            {
                await this.apply(result.Copy());
                Preferences = result.Copy();
                baseline = JsonSerializer.Serialize(Preferences);
                Render();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                System.Diagnostics.Trace.TraceError("Help settings could not be saved: {0}", exception);
                status.Text = UiText.Get("HelpSettingsSaveFailed") + ": " + exception.Message;
            }
        };
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(6), RowSpacing = 5 };
        grid.Children.Add(header);
        Grid.SetRow(viewer, 1); grid.Children.Add(viewer);
        Grid.SetRow(footer, 2); grid.Children.Add(footer);
        Content = grid;
        Dialogs.RegisterNames(this);
        DesktopTheme.Changed += ApplyAppearance;
        Closed += (_, _) => DesktopTheme.Changed -= ApplyAppearance;
        Closing += async (_, e) =>
        {
            if (closingApproved || baseline == JsonSerializer.Serialize(Preferences)) return;
            e.Cancel = true;
            if (saving) return;
            saving = true;
            try
            {
                await this.apply(Preferences.Copy());
                closingApproved = true;
                Close();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                System.Diagnostics.Trace.TraceError("Help settings could not be saved: {0}", exception);
                status.Text = UiText.Get("HelpSettingsSaveFailed") + ": " + exception.Message;
            }
            finally { saving = false; }
        };
        ApplyAppearance();
        Render();
    }

    private void ApplyAppearance()
    {
        viewer.Background = DesktopTheme.Brush("WindowBrush");
        viewer.Foreground = DesktopTheme.Brush("WindowTextBrush");
        viewer.FontSize = 12 * DesktopTheme.TextScale * Preferences.Zoom / 100;
        viewer.TextArea.TextView.Redraw();
    }

    private void Render()
    {
        var text = new StringBuilder();
        headings.Offsets.Clear();
        foreach (var section in document.Sections.Where(section => Preferences.Sections.Contains(section.Kind)))
        {
            if (text.Length > 0) text.Append("\n\n");
            headings.Offsets.Add(text.Length);
            text.Append(UiText.Get("Help" + section.Kind)).Append('\n').Append(section.Text);
        }
        viewer.Text = text.ToString();
        viewer.CaretOffset = 0;
        viewer.ScrollToHome();
        RefreshMatches();
    }

    private void RefreshMatches()
    {
        matchIndex = -1;
        viewer.TextArea.ClearSelection();
        try
        {
            matches = string.IsNullOrEmpty(find.Text) ? [] : ReplacementSearch.Find(viewer.Text, find.Text, "",
                new(MatchCase: Preferences.MatchCase, WholeWord: Preferences.WholeWord));
            status.Text = matches.Count > 0 || string.IsNullOrEmpty(find.Text) ? "" : UiText.Get("NoMatch");
        }
        catch (RegexMatchTimeoutException exception)
        {
            System.Diagnostics.Trace.TraceError("Help search timed out: {0}", exception);
            matches = [];
            status.Text = UiText.Get("HelpSearchTimedOut");
        }
        previous.IsEnabled = next.IsEnabled = matches.Count > 0;
    }

    private void FindNext(bool backwards)
    {
        if (matches.Count == 0) return;
        matchIndex = matchIndex < 0 ? backwards ? matches.Count - 1 : 0
            : (matchIndex + (backwards ? matches.Count - 1 : 1)) % matches.Count;
        var match = matches[matchIndex];
        viewer.Select(match.Start, match.Length);
        viewer.CaretOffset = match.Start + match.Length;
        viewer.ScrollTo(viewer.TextArea.Caret.Line, viewer.TextArea.Caret.Column);
        status.Text = string.Format(UiText.Get("HelpMatchCount"), matchIndex + 1, matches.Count);
    }

    private sealed class HelpHeadings : DocumentColorizingTransformer
    {
        public HashSet<int> Offsets { get; } = [];
        protected override void ColorizeLine(DocumentLine line)
        {
            if (Offsets.Contains(line.Offset) && line.Length > 0)
                ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetTypeface(
                    new Typeface(element.TextRunProperties.Typeface.FontFamily, weight: FontWeight.Bold)));
        }
    }
}
