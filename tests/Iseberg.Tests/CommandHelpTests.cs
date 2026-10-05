using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class CommandHelpTests
{
    private const string HelpFunction = """
        function Test-IsebergHelp {
            <#
            .SYNOPSIS
            Finds a widget.
            .DESCRIPTION
            Finds widgets by their display name.
            .PARAMETER Name
            The widget name to find.
            .INPUTS
            System.String
            .OUTPUTS
            System.String
            .NOTES
            Widget notes.
            .EXAMPLE
            Test-IsebergHelp -Name 'Ada'
            Finds Ada's widget.
            .LINK
            https://example.com/widgets
            #>
            [CmdletBinding()]
            param([Parameter(Mandatory)] [string] $Name)
            $Name
        }
        """;

    [Fact]
    public async Task StructuredHelpRetainsNamedSectionsAndParameterMetadata()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        await session.ExecuteAsync(HelpFunction);
        var document = await session.GetHelpDocumentAsync("Test-IsebergHelp");
        Assert.Equal("Test-IsebergHelp", document.Name);
        Assert.Contains("Finds a widget.", Section(document, HelpSectionKind.Synopsis));
        Assert.Contains("Finds widgets by their display name.", Section(document, HelpSectionKind.Description));
        Assert.Contains("-Name <string>", Section(document, HelpSectionKind.Syntax), StringComparison.OrdinalIgnoreCase);
        var parameters = Section(document, HelpSectionKind.Parameters);
        Assert.Contains("The widget name to find.", parameters);
        Assert.Contains("Required?", parameters);
        Assert.Contains("true", parameters);
        Assert.Contains("System.String", Section(document, HelpSectionKind.Inputs));
        Assert.Contains("System.String", Section(document, HelpSectionKind.Outputs));
        Assert.Contains("Widget notes.", Section(document, HelpSectionKind.Notes));
        Assert.Contains("Test-IsebergHelp -Name 'Ada'", Section(document, HelpSectionKind.Examples));
        Assert.Contains("Finds Ada's widget.", Section(document, HelpSectionKind.Examples));
        Assert.Contains("https://example.com/widgets", Section(document, HelpSectionKind.RelatedLinks));
        var fallback = await session.GetHelpDocumentAsync("Start-Process");
        Assert.Contains("-FilePath", Section(fallback, HelpSectionKind.Syntax));
        Assert.Contains("-FilePath", Section(fallback, HelpSectionKind.Parameters));
        Assert.DoesNotContain("@{", fallback.ToText());
        var about = CommandHelpDocument.FromHelp("about_Test",
            [System.Management.Automation.PSObject.AsPSObject("ABOUT TEST\nA help topic.")]);
        Assert.Contains("A help topic.", Section(about, HelpSectionKind.Remarks));
    }

    [Fact]
    public async Task HelpSettingsPersistAndCopyWithoutSharingSectionLists()
    {
        var path = Path.Combine(Path.GetTempPath(), "iseberg-help-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new UserSettings();
            settings.HelpView.Sections = [HelpSectionKind.Parameters, HelpSectionKind.Examples];
            settings.HelpView.MatchCase = true;
            settings.HelpView.WholeWord = true;
            settings.HelpView.Zoom = 140;
            var copy = settings.Copy();
            copy.HelpView.Sections.Clear();
            Assert.Equal(2, settings.HelpView.Sections.Count);
            await settings.SaveAsync(path);
            var loaded = await UserSettings.LoadAsync(path);
            Assert.Equal(settings.HelpView.Sections, loaded.HelpView.Sections);
            Assert.True(loaded.HelpView.MatchCase);
            Assert.True(loaded.HelpView.WholeWord);
            Assert.Equal(140, loaded.HelpView.Zoom);
            loaded.HelpView.Zoom = double.NaN;
            loaded.Normalize();
            Assert.Equal(100, loaded.HelpView.Zoom);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void CompactFormHasSetTabsLabelValueRowsSwitchesAndIndependentCommonExpander()
    {
        var form = new CommandForm(new("Start-Process", "Start-Process",
        [
            new("Default", true,
            [
                Parameter("FilePath", mandatory: true), Parameter("ArgumentList", array: true),
                Parameter("Credential"), Parameter("LoadUserProfile", kind: CommandParameterKind.Switch),
                Parameter("NoNewWindow", kind: CommandParameterKind.Switch),
                Parameter("PassThru", kind: CommandParameterKind.Switch),
                Parameter("RedirectStandardError"), Parameter("RedirectStandardInput"), Parameter("RedirectStandardOutput"),
                Parameter("Verbose", kind: CommandParameterKind.Switch, common: true)
            ]),
            new("UseShellExecute", false, [Parameter("FilePath", mandatory: true), Parameter("Verb")])
        ]));
        var window = new ShowCommandWindow(form, canInsert: true, _ => Task.CompletedTask);
        try
        {
            Layout(window);
            Assert.Equal("Start-Process", window.Title);
            Assert.Equal(360 * DesktopTheme.TextScale, window.Width);
            Assert.Equal(410 * DesktopTheme.TextScale, window.Height);
            var view = window.FindControl<CommandFormView>("ShowCommandForm")!;
            Assert.True(view.Compact);
            var tabs = view.FindControl<TabStrip>("ParameterSetTabs")!;
            Assert.True(tabs.IsVisible);
            Assert.Equal(2, tabs.ItemCount);
            Assert.False(view.FindControl<ComboBox>("ParameterSetPicker")!.IsVisible);
            Assert.False(view.FindControl<Expander>("PreviewExpander")!.IsVisible);
            var input = Input(view, "FilePath");
            Assert.True(input.Bounds.Width > 140);
            Assert.InRange(input.Bounds.Height, 24, 26);
            var help = window.FindControl<Button>("ShowCommandHelp")!;
            Assert.Equal(UiText.Get("HelpButton"), AutomationProperties.GetName(help));
            Assert.IsType<ToolbarIcon>(help.Content);
            Assert.False(window.FindControl<Button>("ShowCommandRun")!.IsEnabled);
            input.Text = "pwsh";
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Button>("ShowCommandRun")!.IsEnabled);
            var load = view.GetLogicalDescendants().OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "LoadUserProfile");
            load.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("-LoadUserProfile", view.GetCommand());
            load.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("-LoadUserProfile", view.GetCommand());
            var common = view.FindControl<Expander>("CommonParameterExpander")!;
            Assert.False(common.IsExpanded);
            var commonBottom = common.TranslatePoint(default, window)!.Value.Y + common.Bounds.Height;
            var runTop = window.FindControl<Button>("ShowCommandRun")!.TranslatePoint(default, window)!.Value.Y;
            Assert.InRange(runTop - commonBottom, 0, 16);
            var parameters = view.FindControl<StackPanel>("Parameters")!;
            Assert.DoesNotContain(common, parameters.GetLogicalDescendants());
            common.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(common.IsExpanded);
            tabs.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("UseShellExecute", form.SelectedSet.Name);
            Assert.Equal("pwsh", Input(view, "FilePath").Text);
            Assert.DoesNotContain(view.GetLogicalDescendants().OfType<CheckBox>(), c => AutomationProperties.GetName(c) == "LoadUserProfile");
            input = Input(view, "FilePath");
            input.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<Button>("ShowCommandRun")!.IsEnabled);
            Assert.False(form.Value("FilePath").Included);
            var row = view.GetLogicalDescendants().OfType<Grid>().Single(control => AutomationProperties.GetName(control) == "FilePath");
            row.ContextMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var advanced = row.GetLogicalDescendants().OfType<Expander>().Single();
            Assert.True(advanced.IsVisible);
            Assert.True(advanced.IsExpanded);
            var expression = row.GetLogicalDescendants().OfType<CheckBox>()
                .Single(control => AutomationProperties.GetName(control)?.Contains(UiText.Get("PowerShellExpression")) == true);
            expression.IsChecked = true;
            input.Text = "$env:ComSpec";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("-FilePath ($env:ComSpec", view.GetCommand());
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.Control });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, tabs.SelectedIndex);
            Assert.Contains("-FilePath ($env:ComSpec", view.GetCommand());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ParameterSetTabsScrollToTheSelectedSetWhenTheyDoNotFit()
    {
        var sets = Enumerable.Range(0, 12).Select(index => new CommandParameterSetDescription(
            "ParameterSet" + index, index == 0, [])).ToArray();
        var window = new ShowCommandWindow(new(new("Test-ManySets", "Test-ManySets", sets)), false);
        try
        {
            Layout(window);
            var view = window.FindControl<CommandFormView>("ShowCommandForm")!;
            var scroll = view.FindControl<ScrollViewer>("ParameterSetTabScroll")!;
            var tabs = view.FindControl<TabStrip>("ParameterSetTabs")!;
            Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
            tabs.SelectedIndex = 11;
            Layout(window);
            Assert.True(scroll.Offset.X > 0);
            Assert.Equal("ParameterSet11", view.Form!.SelectedSet.Name);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task QuestionMarkOpensHelpWithFindZoomAndSettings()
    {
        var document = Fixture();
        var preferences = new HelpViewSettings();
        var window = new ShowCommandWindow(new(new("Get-Widget", "Get-Widget", [new("Default", true, [])])), false,
            owner => new CommandHelpWindow(document, preferences).ShowDialog(owner));
        window.Show();
        try
        {
            window.FindControl<Button>("ShowCommandHelp")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.OwnedWindows.OfType<CommandHelpWindow>().Any());
            var help = window.OwnedWindows.OfType<CommandHelpWindow>().Single();
            Layout(help);
            Assert.Equal("Get-Widget Help", help.Title);
            Assert.True(help.FindControl<TextEditor>("HelpContent")!.IsReadOnly);
            Assert.NotNull(help.FindControl<TextBox>("HelpFind"));
            Assert.NotNull(help.FindControl<Button>("HelpPrevious"));
            Assert.NotNull(help.FindControl<Button>("HelpNext"));
            Assert.NotNull(help.FindControl<Slider>("HelpZoom"));
            help.FindControl<Button>("HelpSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => help.OwnedWindows.OfType<HelpSettingsWindow>().Any());
            var settings = help.OwnedWindows.OfType<HelpSettingsWindow>().Single();
            Layout(settings);
            Assert.Equal(10, settings.GetLogicalDescendants().OfType<CheckBox>().Count(c => c.Name?.StartsWith("HelpSection") == true));
            Assert.True(settings.FindControl<CheckBox>("HelpSectionSynopsis")!.IsChecked);
            Assert.True(settings.FindControl<CheckBox>("HelpSectionOutputs")!.IsChecked);
            Assert.True(settings.FindControl<CheckBox>("HelpSectionOutputs")!.Bounds.Width > 40);
            var left = settings.FindControl<CheckBox>("HelpSectionSynopsis")!.TranslatePoint(default, settings)!.Value;
            var right = settings.FindControl<CheckBox>("HelpSectionOutputs")!.TranslatePoint(default, settings)!.Value;
            Assert.True(right.X > left.X + 60);
            settings.Close();
            help.Close();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HelpFindNavigatesWrapsAndUsesSavedCaseAndWholeWordOptions()
    {
        var window = new CommandHelpWindow(Fixture(), new());
        try
        {
            Layout(window);
            var find = window.FindControl<TextBox>("HelpFind")!;
            var viewer = window.FindControl<TextEditor>("HelpContent")!;
            var previous = window.FindControl<Button>("HelpPrevious")!;
            var next = window.FindControl<Button>("HelpNext")!;
            Assert.False(next.IsEnabled);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F, KeyModifiers = KeyModifiers.Control });
            Assert.True(find.IsFocused);
            find.Text = "widget";
            Dispatcher.UIThread.RunJobs();
            Assert.True(next.IsEnabled);
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Widget", viewer.SelectedText);
            var first = viewer.SelectionStart;
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(viewer.SelectionStart > first);
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(first, viewer.SelectionStart);
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(viewer.SelectionStart > first);
            find.Text = "absent";
            Dispatcher.UIThread.RunJobs();
            Assert.False(next.IsEnabled);
            Assert.Equal(UiText.Get("NoMatch"), window.FindControl<TextBlock>("HelpSearchStatus")!.Text);
            window.FindControl<Button>("HelpSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.OwnedWindows.OfType<HelpSettingsWindow>().Any());
            var settings = window.OwnedWindows.OfType<HelpSettingsWindow>().Single();
            settings.FindControl<CheckBox>("HelpMatchCase")!.IsChecked = true;
            settings.FindControl<CheckBox>("HelpWholeWord")!.IsChecked = true;
            settings.FindControl<Button>("AcceptHelpSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.Preferences.MatchCase);
            find.Text = "widget";
            Dispatcher.UIThread.RunJobs();
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("widget", viewer.SelectedText);
            Assert.Contains("1 of 1", window.FindControl<TextBlock>("HelpSearchStatus")!.Text);
            var zoom = window.FindControl<Slider>("HelpZoom")!;
            zoom.Value = 150;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(18 * DesktopTheme.TextScale, viewer.FontSize);
            Assert.Equal("150%", window.FindControl<TextBlock>("HelpZoomText")!.Text);
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F3 };
            window.RaiseEvent(key);
            Assert.True(key.Handled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HelpSettingsCancelIsIsolatedAndOkFiltersSectionsAndPersistsTheDraft()
    {
        var saved = new List<HelpViewSettings>();
        var window = new CommandHelpWindow(Fixture(), new(), draft => { saved.Add(draft.Copy()); return Task.CompletedTask; });
        try
        {
            Layout(window);
            var viewer = window.FindControl<TextEditor>("HelpContent")!;
            window.FindControl<Button>("HelpSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.OwnedWindows.OfType<HelpSettingsWindow>().Any());
            var settings = window.OwnedWindows.OfType<HelpSettingsWindow>().Single();
            settings.FindControl<CheckBox>("HelpSectionSynopsis")!.IsChecked = false;
            settings.FindControl<CheckBox>("HelpMatchCase")!.IsChecked = true;
            settings.FindControl<Button>("CancelHelpSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(saved);
            Assert.False(window.Preferences.MatchCase);
            Assert.Contains("Widget widgets widget", viewer.Text);
            window.FindControl<Button>("HelpSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.OwnedWindows.OfType<HelpSettingsWindow>().Any());
            settings = window.OwnedWindows.OfType<HelpSettingsWindow>().Single();
            settings.FindControl<CheckBox>("HelpSectionSynopsis")!.IsChecked = false;
            settings.FindControl<CheckBox>("HelpMatchCase")!.IsChecked = true;
            settings.FindControl<Button>("AcceptHelpSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => saved.Count == 1);
            Assert.DoesNotContain(HelpSectionKind.Synopsis, window.Preferences.Sections);
            Assert.True(saved[0].MatchCase);
            Assert.DoesNotContain("Widget widgets widget", viewer.Text);
            Assert.Contains("-Name <string>", viewer.Text);
            window.FindControl<Slider>("HelpZoom")!.Value = 130;
            window.Close();
            await WaitFor(() => saved.Count == 2);
            Assert.Equal(130, saved[1].Zoom);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DisablingAllHelpSectionsShowsAnEmptyDocumentWithoutStaleMatches()
    {
        var window = new CommandHelpWindow(Fixture(), new());
        try
        {
            Layout(window);
            window.FindControl<TextBox>("HelpFind")!.Text = "widget";
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Button>("HelpNext")!.IsEnabled);
            window.FindControl<Button>("HelpSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.OwnedWindows.OfType<HelpSettingsWindow>().Any());
            var settings = window.OwnedWindows.OfType<HelpSettingsWindow>().Single();
            foreach (var check in settings.GetLogicalDescendants().OfType<CheckBox>()
                .Where(control => control.Name?.StartsWith("HelpSection") == true))
                check.IsChecked = false;
            settings.FindControl<Button>("AcceptHelpSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.Preferences.Sections.Count == 0);
            Assert.Equal("", window.FindControl<TextEditor>("HelpContent")!.Text);
            Assert.False(window.FindControl<Button>("HelpNext")!.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HelpSettingsSaveFailureIsVisibleAndDoesNotApplyTheDraft()
    {
        var window = new CommandHelpWindow(Fixture(), new(), _ => throw new IOException("test-save-failure"));
        try
        {
            Layout(window);
            window.FindControl<Button>("HelpSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.OwnedWindows.OfType<HelpSettingsWindow>().Any());
            var settings = window.OwnedWindows.OfType<HelpSettingsWindow>().Single();
            settings.FindControl<CheckBox>("HelpSectionSynopsis")!.IsChecked = false;
            settings.FindControl<Button>("AcceptHelpSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.FindControl<TextBlock>("HelpSearchStatus")!.Text?.Contains("test-save-failure") == true);
            Assert.Contains(HelpSectionKind.Synopsis, window.Preferences.Sections);
            Assert.Contains("Widget widgets widget", window.FindControl<TextEditor>("HelpContent")!.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ConsoleHelpUsesPrecomputedSectionsWhileTheRunspaceIsOccupied()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("First");
        try
        {
            await session.Engine.InitializeAsync();
            await session.Engine.ExecuteAsync(HelpFunction);
            window.Workbench.Sessions.Add(session);
            window.Workbench.SelectedSession = session;
            window.Show();
            var execution = session.Engine.ExecuteAsync("Show-Command Test-IsebergHelp");
            await WaitFor(() => window.OwnedWindows.OfType<ShowCommandWindow>().Any());
            var command = window.OwnedWindows.OfType<ShowCommandWindow>().Single();
            command.FindControl<Button>("ShowCommandHelp")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => command.OwnedWindows.OfType<CommandHelpWindow>().Any());
            var help = command.OwnedWindows.OfType<CommandHelpWindow>().Single();
            Assert.Equal(SessionState.Running, session.Engine.State);
            Assert.Contains("Finds a widget.", help.FindControl<TextEditor>("HelpContent")!.Text);
            await session.Engine.StopAsync();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitFor(() => !window.OwnedWindows.OfType<ShowCommandWindow>().Any());
            Assert.False(help.IsVisible);
        }
        finally { window.Close(); await session.Engine.DisposeAsync(); }
    }

    private static string Section(CommandHelpDocument document, HelpSectionKind kind) =>
        Assert.Single(document.Sections, section => section.Kind == kind).Text;

    private static CommandHelpDocument Fixture() => new("Get-Widget",
    [
        new(HelpSectionKind.Synopsis, "Widget widgets widget"),
        new(HelpSectionKind.Syntax, "Get-Widget -Name <string>"),
        new(HelpSectionKind.Parameters, "-Name <string>\n    Required? true"),
        new(HelpSectionKind.Examples, "Get-Widget -Name 'Ada'")
    ]);

    private static CommandParameterDescription Parameter(string name, bool mandatory = false,
        CommandParameterKind kind = CommandParameterKind.Text, bool array = false, bool common = false) =>
        new(name, array ? "String[]" : kind == CommandParameterKind.Switch ? "SwitchParameter" : "String", kind,
            mandatory, null, array, common, false, false, "", [], []);

    private static TextBox Input(Control view, string name) =>
        view.GetLogicalDescendants().OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == name);

    private static void Layout(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var content = Assert.IsAssignableFrom<Control>(window.Content);
        var size = new Size(window.Width, double.IsNaN(window.Height) ? 350 : window.Height);
        content.Measure(size);
        content.Arrange(new Rect(default, size));
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
}
