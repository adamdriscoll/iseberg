using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class OptionsTests
{
    [AvaloniaFact]
    public void DialogMatchesReferenceGeometryAndInitialState()
    {
        var dialog = new OptionsWindow();
        try
        {
            Layout(dialog);
            Assert.Equal(534 * DesktopTheme.TextScale, dialog.Width);
            Assert.Equal(562 * DesktopTheme.TextScale, dialog.Height);
            Assert.False(dialog.CanResize);
            AssertRect(dialog, "ColorTree", 3, 25, 222, 200);
            AssertRect(dialog, "EditorFont", 8, 251, 275, 22);
            AssertRect(dialog, "EditorFontSize", 293, 251, 85, 22);
            AssertRect(dialog, "ThemeName", 3, 299, 419, 28);
            AssertRect(dialog, "SampleEditor", 3, 352, 528, 159);
            AssertRect(dialog, "RestoreDefaults", 10, 529, 98, 22);
            AssertRect(dialog, "AcceptOptions", 363, 529, 47, 22);
            Assert.False(dialog.FindControl<Button>("AcceptOptions")!.IsEnabled);
            Assert.False(dialog.FindControl<Button>("ApplyOptions")!.IsEnabled);
            Assert.False(dialog.FindControl<Canvas>("ColorControls")!.IsEnabled);
            Assert.Equal(9d, dialog.FindControl<ComboBox>("EditorFontSize")!.SelectedItem);
            Assert.Equal("Lucida Console", dialog.FindControl<ComboBox>("EditorFont")!.SelectedItem);
            Assert.Equal(1, dialog.FindControl<TextEditor>("SampleEditor")!.Options.LineHeightFactor);
            Assert.Equal(12, dialog.FindControl<TabStrip>("OptionsTabs")!.GetVisualDescendants().OfType<TabStripItem>().First().FontSize);

            dialog.FindControl<TabStrip>("OptionsTabs")!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<Canvas>("GeneralPage")!.IsVisible);
            AssertRect(dialog, "BehaviorGroup", 4, 34, 526, 155);
            AssertRect(dialog, "IntelliSenseGroup", 4, 199, 526, 155);
            AssertRect(dialog, "OtherGroup", 4, 364, 526, 155);
            AssertRect(dialog, "ShowOutlining", 50, 47, dialog.FindControl<CheckBox>("ShowOutlining")!.Bounds.Width, 16);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void ColorTreeHasCompactExpandedRootsAndAllThemeColors()
    {
        var dialog = new OptionsWindow();
        try
        {
            Layout(dialog);
            var tree = dialog.FindControl<TreeView>("ColorTree")!;
            var nodes = Walk(tree.Items.OfType<TreeViewItem>()).ToArray();
            Assert.Equal(new EditorTheme().Colors.Keys.Order(), nodes.Select(n => n.Tag as string).Where(k => k is not null).Order());
            Assert.All(tree.Items.OfType<TreeViewItem>(), node => Assert.True(node.IsExpanded));
            var first = nodes.First(n => n.Tag as string == "Script.Foreground");
            Assert.Equal(16, first.Bounds.Height);
            tree.SelectedItem = first;
            Assert.True(dialog.FindControl<Canvas>("ColorControls")!.IsEnabled);
            Assert.Equal("0", dialog.FindControl<TextBox>("RedValue")!.Text);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void ColorEditingValidatesAndHexadecimalRoundTrips()
    {
        var dialog = new OptionsWindow();
        try
        {
            Layout(dialog);
            var tree = dialog.FindControl<TreeView>("ColorTree")!;
            tree.SelectedItem = Walk(tree.Items.OfType<TreeViewItem>()).First(n => n.Tag as string == "Script.Foreground");
            var slider = dialog.FindControl<Slider>("RedSlider")!;
            var track = slider.GetVisualDescendants().OfType<Track>().Single();
            Assert.Equal(Avalonia.Layout.Orientation.Horizontal, track.Orientation);
            Assert.Equal(0, track.Thumb!.Bounds.X, 2);
            dialog.FindControl<TextBox>("RedValue")!.Text = "255";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("#FF0000", dialog.Draft.Theme.Colors["Script.Foreground"]);
            Layout(dialog);
            Assert.Equal(track.Bounds.Width, track.Thumb.Bounds.Right, 2);
            dialog.FindControl<CheckBox>("Hexadecimal")!.IsChecked = true;
            Assert.Equal("FF", dialog.FindControl<TextBox>("RedValue")!.Text);
            dialog.FindControl<TextBox>("GreenValue")!.Text = "80";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("#FF8000", dialog.Draft.Theme.Colors["Script.Foreground"]);
            dialog.FindControl<TextBox>("BlueValue")!.Text = "GG";
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<Border>("ValidationPanel")!.IsVisible);
            Assert.False(dialog.FindControl<Button>("ApplyOptions")!.IsEnabled);
            dialog.FindControl<TextBox>("BlueValue")!.Text = "00";
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<Button>("ApplyOptions")!.IsEnabled);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void CancelDiscardsUnappliedChangesAndDoesNotMutateCaller()
    {
        var settings = new UserSettings();
        var applied = false;
        var dialog = new OptionsWindow(settings, _ => { applied = true; return Task.CompletedTask; });
        Layout(dialog);
        dialog.FindControl<CheckBox>("ShowToolbar")!.IsChecked = false;
        Assert.False(dialog.Draft.ShowToolbar);
        dialog.FindControl<Button>("CancelOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(applied);
        Assert.True(settings.ShowToolbar);
    }

    [AvaloniaFact]
    public void ApplyCommitsBothTabsAndCancelKeepsTheAppliedSnapshot()
    {
        UserSettings? applied = null;
        var dialog = new OptionsWindow(new UserSettings(), value => { applied = value; return Task.CompletedTask; });
        Layout(dialog);
        dialog.FindControl<CheckBox>("ShowLineNumbers")!.IsChecked = false;
        dialog.FindControl<ComboBox>("EditorFontSize")!.SelectedItem = 14d;
        dialog.FindControl<TextBox>("RecentFileCount")!.Text = "25";
        dialog.FindControl<Button>("ApplyOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(applied);
        Assert.False(applied.ShowLineNumbers);
        Assert.Equal(14, applied.FontSize);
        Assert.Equal(25, applied.RecentFileCount);
        Assert.False(dialog.FindControl<Button>("ApplyOptions")!.IsEnabled);
        dialog.FindControl<CheckBox>("ShowLineNumbers")!.IsChecked = true;
        dialog.FindControl<Button>("CancelOptions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(applied.ShowLineNumbers);
    }

    [AvaloniaFact]
    public void GeneralValidationAndCompletionDependenciesAreExplicit()
    {
        var dialog = new OptionsWindow();
        try
        {
            Layout(dialog);
            dialog.FindControl<CheckBox>("ConsoleIntelliSense")!.IsChecked = false;
            Assert.False(dialog.FindControl<CheckBox>("ConsoleEnterSelects")!.IsEnabled);
            Assert.True(dialog.Draft.ConsoleCompletionOnEnter);
            dialog.FindControl<TextBox>("AutoSaveInterval")!.Text = "-1";
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<Button>("AcceptOptions")!.IsEnabled);
            Assert.True(dialog.FindControl<Border>("ValidationPanel")!.IsVisible);
            dialog.FindControl<TextBox>("AutoSaveInterval")!.Text = "0";
            dialog.FindControl<TextBox>("RecentFileCount")!.Text = "101";
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<Button>("AcceptOptions")!.IsEnabled);
            dialog.FindControl<TextBox>("RecentFileCount")!.Text = "0";
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<Button>("AcceptOptions")!.IsEnabled);
            Assert.Equal(0, dialog.Draft.AutoSaveMinutes);
            Assert.Equal(0, dialog.Draft.RecentFileCount);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void RestoreDefaultsPreservesUnrelatedPreferencesAndSavedThemes()
    {
        var dialog = new OptionsWindow(new UserSettings
        {
            ShowToolbar = false, FontSize = 20, LoadProfiles = true, Zoom = 140,
            CustomThemes = [new() { Name = "Saved theme" }], RecentFiles = ["example.ps1"],
            DebuggerSessions = [new() { Name = "PowerShell 1", Watches = ["$value"], Breakpoints = [new(BreakpointKind.Command, Target: "Get-Process")] }]
        }, _ => Task.CompletedTask);
        try
        {
            Layout(dialog);
            dialog.FindControl<Button>("RestoreDefaults")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.Draft.ShowToolbar);
            Assert.Equal(9, dialog.Draft.FontSize);
            Assert.True(dialog.Draft.LoadProfiles);
            Assert.Equal(140, dialog.Draft.Zoom);
            Assert.Single(dialog.Draft.CustomThemes);
            Assert.Single(dialog.Draft.RecentFiles);
            Assert.Equal("$value", dialog.Draft.DebuggerSessions.Single().Watches.Single());
            Assert.Equal("Get-Process", dialog.Draft.DebuggerSessions.Single().Breakpoints.Single().Target);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void CtrlTabSwitchesOptionsPagesAndEditorsExposeAccessibleValues()
    {
        var dialog = new OptionsWindow();
        try
        {
            Layout(dialog);
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.Control };
            dialog.RaiseEvent(key);
            Assert.True(key.Handled);
            Assert.Equal(1, dialog.FindControl<TabStrip>("OptionsTabs")!.SelectedIndex);
            var sample = dialog.FindControl<AccessibleTextEditor>("SampleEditor")!;
            var peer = ControlAutomationPeer.CreatePeerForElement(sample)!;
            Assert.Equal("Sample:", peer.GetName());
            Assert.Equal(AutomationControlType.Edit, peer.GetAutomationControlType());
            var provider = peer.GetProvider<IValueProvider>()!;
            Assert.True(provider.IsReadOnly);
            Assert.Contains("MyFunction", provider.Value);
            Assert.Throws<InvalidOperationException>(() => provider.SetValue("Cannot edit"));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void HighContrastOverridesCustomEditorColorsWithoutChangingPreferences()
    {
        var window = new MainWindow([], initializeOnOpen: false, preferences: new UserSettings
        {
            Theme = new() { Colors = new EditorTheme().Colors.ToDictionary(p => p.Key, p => "#FF00FF") }
        });
        try
        {
            window.Show();
            DesktopTheme.Refresh(highContrast: true);
            Assert.Equal(DesktopTheme.Brush("WindowBrush"), window.FindControl<TextEditor>("ScriptEditor")!.Background);
            Assert.Equal(DesktopTheme.Brush("WindowTextBrush"), window.FindControl<TextEditor>("ConsoleEditor")!.Foreground);
            Assert.False(window.FindControl<TextEditor>("ScriptEditor")!.Options.HighlightCurrentLine);
            DesktopTheme.Refresh(highContrast: false);
            Assert.Equal(Color.Parse("#FF00FF"), ((ISolidColorBrush)window.FindControl<TextEditor>("ScriptEditor")!.Background!).Color);
        }
        finally { DesktopTheme.Refresh(); window.Close(); }
    }

    [AvaloniaFact]
    public void ToolbarAndMenuStateFollowPreferencesAndF10FocusesTheMenu()
    {
        var window = new MainWindow([], initializeOnOpen: false, preferences: new UserSettings { ShowToolbar = false, ShowLineNumbers = false });
        try
        {
            window.Show();
            Assert.False(window.FindControl<Border>("WorkbenchToolbar")!.IsVisible);
            var menu = window.FindControl<Menu>("WorkbenchMenu")!;
            var lineNumbers = menu.Items.OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>()).Single(m => m.Tag as string == "LineNumbers");
            Assert.False(lineNumbers.IsChecked);
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F10 };
            window.RaiseEvent(key);
            Assert.True(key.Handled);
            Assert.Equal(0, menu.SelectedIndex);
            Assert.All(window.FindControl<Border>("WorkbenchToolbar")!.GetVisualDescendants().OfType<Button>(), b => Assert.False(b.Focusable));
        }

        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MenuActionsKeepTheLastTextTargetAndReadOnlyState()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var input = window.FindControl<TextEditor>("ConsoleEditor")!;
            input.Text = "Get-Process";
            input.TextArea.Focus();
            var menu = window.FindControl<Menu>("WorkbenchMenu")!;
            var edit = menu.Items.OfType<MenuItem>().Single(m => m.Header as string == UiText.Get("EditMenu"));
            edit.Focus();
            edit.Items.OfType<MenuItem>().Single(m => m.Tag as string == "SelectAll").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(0, input.SelectionStart);
            Assert.Equal(input.Text.Length, input.SelectionLength);
            Assert.True(input.TextArea.IsFocused);
            Assert.Equal(0, window.FindControl<TextEditor>("ScriptEditor")!.SelectionLength);
            var output = window.FindControl<TextEditor>("ConsoleEditor")!;
            output.IsReadOnly = true;
            Assert.True(output.TextArea.Focus());
            Assert.True(output.IsReadOnly);
            Assert.True(output.TextArea.IsFocused);
            Assert.False(edit.Items.OfType<MenuItem>().Single(m => m.Tag as string == "Paste").IsEnabled);
            edit.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(edit.Items.OfType<MenuItem>().Single(m => m.Tag as string == "Paste").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EveryActionIconRendersAGlyph()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        try
        {
            var icons = window.FindControl<Menu>("WorkbenchMenu")!.Items.OfType<MenuItem>()
                .SelectMany(m => m.Items.OfType<MenuItem>()).Select(m => m.Icon).OfType<ToolbarIcon>();
            foreach (var icon in icons)
            {
                icon.Measure(new Size(18, 18));
                icon.Arrange(new Rect(0, 0, 18, 18));
                var drawing = new DrawingGroup();
                using (var context = drawing.Open()) icon.Render(context);
                Assert.NotEmpty(drawing.Children);
            }
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task SettingsRoundTripThemeAndGeneralPreferencesAndMigrateOlderFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "iseberg-options-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new UserSettings
            {
                ShowOutlining = false, WarnDuplicateFiles = false, PromptToSaveBeforeRun = false,
                ConsoleIntelliSense = false, ScriptCompletionOnEnter = false, IntelliSenseTimeoutSeconds = 7,
                UseLocalHelp = false, ShowToolbar = false, UseDefaultSnippets = false, AutoSaveMinutes = 0,
                RecentFileCount = 25, FontFamily = "Consolas", FontSize = 14, FixedWidthFontsOnly = true
            };
            settings.Theme.Colors["Script.Foreground"] = "#123456";
            settings.CustomThemes.Add(settings.Theme.Copy());
            await settings.SaveAsync(path);
            var loaded = await UserSettings.LoadAsync(path);
            Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(loaded));
            await File.WriteAllTextAsync(path, """{"Zoom":125,"Layout":"Right","LoadProfiles":true}""");
            loaded = await UserSettings.LoadAsync(path);
            Assert.Equal(125, loaded.Zoom);
            Assert.True(loaded.LoadProfiles);
            Assert.True(loaded.ShowOutlining);
            Assert.Equal(9, loaded.FontSize);
            Assert.Equal("#FFFFFF", loaded.Theme.Colors["Script.Background"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SettingsNormalizeInvalidNumbersAndThemeColors()
    {
        var settings = new UserSettings { FontSize = double.NaN, Zoom = double.PositiveInfinity, AutoSaveMinutes = -1, RecentFileCount = 1000 };
        settings.Theme.Colors = new() { ["Script.Foreground"] = "invalid" };
        settings.Normalize();
        Assert.Equal(9, settings.FontSize);
        Assert.Equal(100, settings.Zoom);
        Assert.Equal(0, settings.AutoSaveMinutes);
        Assert.Equal(100, settings.RecentFileCount);
        Assert.Equal(new EditorTheme().Colors, settings.Theme.Colors);
    }

    [Fact]
    public async Task AutosaveUsesRecoveryCopiesAndDoesNotStealALiveInstancesSnapshots()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-recovery-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var store = new ScriptRecovery(directory);
        try
        {
            var file = new ScriptFile("Unsaved.ps1") { Text = "Write-Output 'recovery'" };
            await store.SaveAsync(id, file);
            Assert.Null(file.Path);
            Assert.True(file.IsDirty);
            Assert.Empty(await store.ReadAsync());
            var path = Path.Combine(directory, id.ToString("N") + ".json");
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            }
            var saved = JsonSerializer.Deserialize<RecoveredScript>(await File.ReadAllTextAsync(path))!;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(saved with { OwnerProcessId = 0 }));
            Assert.Equal(file.Text, (await store.ReadAsync()).Single().Text);
            store.Remove(id);
            Assert.Empty(await store.ReadAsync());
        }
        finally
        {
            store.Remove(id);
            File.Delete(Path.Combine(directory, id.ToString("N") + ".json.tmp"));
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void EnglishResourcesFallbackForAnUntranslatedCulture()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("General Settings", UiText.Get("GeneralSettings"));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private static IEnumerable<TreeViewItem> Walk(IEnumerable<TreeViewItem> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Walk(node.Items.OfType<TreeViewItem>())) yield return child;
        }
    }

    private static void Layout(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var content = Assert.IsAssignableFrom<Control>(window.Content);
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertRect(Window window, string name, double x, double y, double width, double height)
    {
        var control = window.FindControl<Control>(name)!;
        var point = control.TranslatePoint(default, window)!.Value;
        Assert.Equal(x * DesktopTheme.TextScale, point.X, 2);
        Assert.Equal(y * DesktopTheme.TextScale, point.Y, 2);
        Assert.Equal(width, control.Bounds.Width, 2);
        Assert.Equal(height, control.Bounds.Height, 2);
    }
}
