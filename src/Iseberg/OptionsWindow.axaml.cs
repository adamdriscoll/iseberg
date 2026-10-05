using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class OptionsWindow : Window
{
    private readonly Func<UserSettings, Task> apply;
    private UserSettings draft;
    private string baseline;
    private bool updating;
    private bool saving;
    private bool colorValid = true;
    private string? colorKey;
    private readonly PowerShellColorizer sampleColorizer = new();
    private readonly string[] layouts = ["Top", "Right", "Maximized"];
    public UserSettings Draft => draft.Copy();

    public OptionsWindow() : this(new UserSettings(), _ => Task.CompletedTask) { }

    public OptionsWindow(UserSettings settings, Func<UserSettings, Task> apply)
    {
        this.apply = apply;
        draft = settings.Copy();
        draft.Normalize();
        baseline = JsonSerializer.Serialize(draft);
        InitializeComponent();
        Icon = AppIcon.Create();
        BuildColorTree();
        EditorFontSize.ItemsSource = new double[] { 6, 7, 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72 }
            .Append(draft.FontSize).Distinct().Order().ToArray();
        PanePosition.ItemsSource = layouts.Select(UiText.Get).ToArray();
        CompletionTimeout.ItemsSource = Enumerable.Range(1, 30).ToArray();
        NamedColor.ItemsSource = typeof(Colors).GetProperties().Where(p => p.PropertyType == typeof(Color) && p.Name != "Transparent")
            .Select(p => p.Name).Order().ToArray();
        SampleEditor.Text = """
            # This is a PowerShell comment.

            function MyFunction([Parameter(Position = 0)][System.String]$path)
            {
                :looplabel foreach ($thisFile in (Get-ChildItem $path))
                {
                    Write-Host ; Write-Host -Fore Yellow `
                        ('Length:' +
                        [System.Math]::Floor($thisFile.Length / 1000))
                }
            }
            """;
        sampleColorizer.Analysis = EditorAnalysis.Analyze(SampleEditor.Text);
        SampleEditor.TextArea.TextView.LineTransformers.Add(sampleColorizer);
        SampleEditor.Options.AllowScrollBelowDocument = false;
        SampleEditor.Options.LineHeightFactor = 1;
        LoadControls();
        OptionsTabs.SelectionChanged += (_, _) =>
        {
            ColorsPage.IsVisible = OptionsTabs.SelectedIndex == 0;
            GeneralPage.IsVisible = OptionsTabs.SelectedIndex == 1;
        };
        ColorTree.SelectionChanged += (_, _) => SelectColor();
        foreach (var box in new[] { ShowOutlining, ShowLineNumbers, WarnDuplicates, PromptToSave, ConsoleIntelliSense,
                     ConsoleEnterSelects, ScriptIntelliSense, ScriptEnterSelects, LocalHelp, ShowToolbar, DefaultSnippets, FixedWidthOnly })
            box.IsCheckedChanged += (_, _) =>
            {
                if (updating) return;
                if (box == FixedWidthOnly) PopulateFonts();
                UpdateDraft();
            };
        foreach (var combo in new[] { EditorFont, EditorFontSize, PanePosition, CompletionTimeout })
            combo.SelectionChanged += (_, _) => UpdateDraft();
        AutoSaveInterval.TextChanged += (_, _) => UpdateDraft();
        RecentFileCount.TextChanged += (_, _) => UpdateDraft();
        foreach (var box in new[] { RedValue, GreenValue, BlueValue })
            box.TextChanged += (_, _) => ReadColor();
        foreach (var slider in new[] { RedSlider, GreenSlider, BlueSlider })
            slider.ValueChanged += (_, _) =>
            {
                if (updating || colorKey is null) return;
                SetColor(Color.FromRgb((byte)Math.Round(RedSlider.Value), (byte)Math.Round(GreenSlider.Value), (byte)Math.Round(BlueSlider.Value)));
            };
        Hexadecimal.IsCheckedChanged += (_, _) =>
        {
            if (!updating && colorKey is not null)
            {
                colorValid = true;
                DisplayColor(Color.Parse(draft.Theme.Colors[colorKey]));
                UpdateDraft();
            }
        };
        NamedColor.SelectionChanged += (_, _) =>
        {
            if (!updating && NamedColor.SelectedItem is string color) SetColor(Color.Parse(color));
        };
        RestoreDefaults.Click += (_, _) => Restore();
        CancelOptions.Click += (_, _) => Close(false);
        AcceptOptions.Click += async (_, _) => { if (await ApplyAsync()) Close(true); };
        ApplyOptions.Click += async (_, _) => await ApplyAsync();
        ManageThemes.Click += async (_, _) => await ManageThemesAsync();
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
        DesktopTheme.Changed += RefreshSample;
        Closed += (_, _) => DesktopTheme.Changed -= RefreshSample;
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        Opened += (_, _) => OptionsTabs.Focus();
    }

    private void BuildColorTree()
    {
        TreeViewItem Leaf(string key, string label, bool bold = false) => new()
        {
            Tag = key, Header = UiText.Get(label), FontWeight = bold ? FontWeight.Bold : FontWeight.Normal
        };
        TreeViewItem Tokens(string prefix, string title) => new()
        {
            Header = UiText.Get(title),
            ItemsSource = new[] { "Comment", "Keyword", "String", "Variable", "Number", "Command", "CommandArgument", "Function", "Attribute", "Parameter", "Type", "Operator", "Member", "Label" }
                .Select(token => Leaf(prefix + "." + token, token)).ToArray()
        };
        ColorTree.ItemsSource = new[]
        {
            new TreeViewItem
            {
                Header = UiText.Get("ScriptPane"), IsExpanded = true,
                ItemsSource = new[]
                {
                    Leaf("Script.Foreground", "Foreground", true), Leaf("Script.Background", "Background", true),
                    Tokens("Script", "ScriptTokens"),
                    new TreeViewItem { Header = UiText.Get("XmlTokens"), ItemsSource = new[] { "Comment", "Tag", "Attribute", "Value" }.Select(t => Leaf("Xml." + t, t)).ToArray() }
                }
            },
            new TreeViewItem
            {
                Header = UiText.Get("ConsolePane"), IsExpanded = true,
                ItemsSource = new[]
                {
                    Leaf("Console.Foreground", "Foreground", true), Leaf("Console.Background", "Background", true),
                    Leaf("Console.TextBackground", "TextBackground", true), Tokens("Console", "ConsoleTokens"),
                    new TreeViewItem { Header = UiText.Get("OutputStreams"), ItemsSource = new[] { "Error", "Warning", "Verbose", "Debug" }.Select(t => Leaf("Stream." + t, t)).ToArray() }
                }
            }
        };
    }

    private void LoadControls()
    {
        updating = true;
        ShowOutlining.IsChecked = draft.ShowOutlining;
        ShowLineNumbers.IsChecked = draft.ShowLineNumbers;
        WarnDuplicates.IsChecked = draft.WarnDuplicateFiles;
        PromptToSave.IsChecked = draft.PromptToSaveBeforeRun;
        ConsoleIntelliSense.IsChecked = draft.ConsoleIntelliSense;
        ConsoleEnterSelects.IsChecked = draft.ConsoleCompletionOnEnter;
        ScriptIntelliSense.IsChecked = draft.ScriptIntelliSense;
        ScriptEnterSelects.IsChecked = draft.ScriptCompletionOnEnter;
        LocalHelp.IsChecked = draft.UseLocalHelp;
        ShowToolbar.IsChecked = draft.ShowToolbar;
        DefaultSnippets.IsChecked = draft.UseDefaultSnippets;
        FixedWidthOnly.IsChecked = draft.FixedWidthFontsOnly;
        PanePosition.SelectedIndex = Array.IndexOf(layouts, draft.Layout);
        CompletionTimeout.SelectedItem = draft.IntelliSenseTimeoutSeconds;
        AutoSaveInterval.Text = draft.AutoSaveMinutes.ToString(CultureInfo.InvariantCulture);
        RecentFileCount.Text = draft.RecentFileCount.ToString(CultureInfo.InvariantCulture);
        EditorFontSize.SelectedItem = draft.FontSize;
        PopulateFonts();
        updating = false;
        RefreshSample();
        if (colorKey is not null) DisplayColor(Color.Parse(draft.Theme.Colors[colorKey]));
        UpdateDraft();
    }

    private void PopulateFonts()
    {
        var wasUpdating = updating;
        updating = true;
        var selected = draft.FontFamily;
        var fonts = FontManager.Current.SystemFonts.Select(f => f.Name)
            .Where(f => FixedWidthOnly.IsChecked != true || IsFixedWidth(f))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (FixedWidthOnly.IsChecked != true) fonts = fonts.Append(selected).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (fonts.Length == 0) fonts = ["Consolas"];
        EditorFont.ItemsSource = fonts;
        EditorFont.SelectedItem = fonts.FirstOrDefault(f => string.Equals(f, selected, StringComparison.OrdinalIgnoreCase)) ?? fonts[0];
        updating = wasUpdating;
    }

    private static bool IsFixedWidth(string font)
    {
        double Width(string text) => new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(font), 12, Brushes.Black).Width;
        return Math.Abs(Width("iiiiiiii") - Width("WWWWWWWW")) < 0.1;
    }

    private void UpdateDraft()
    {
        if (updating) return;
        draft.ShowOutlining = ShowOutlining.IsChecked == true;
        draft.ShowLineNumbers = ShowLineNumbers.IsChecked == true;
        draft.WarnDuplicateFiles = WarnDuplicates.IsChecked == true;
        draft.PromptToSaveBeforeRun = PromptToSave.IsChecked == true;
        draft.ConsoleIntelliSense = ConsoleIntelliSense.IsChecked == true;
        draft.ConsoleCompletionOnEnter = ConsoleEnterSelects.IsChecked == true;
        draft.ScriptIntelliSense = ScriptIntelliSense.IsChecked == true;
        draft.ScriptCompletionOnEnter = ScriptEnterSelects.IsChecked == true;
        draft.UseLocalHelp = LocalHelp.IsChecked == true;
        draft.ShowToolbar = ShowToolbar.IsChecked == true;
        draft.UseDefaultSnippets = DefaultSnippets.IsChecked == true;
        draft.FixedWidthFontsOnly = FixedWidthOnly.IsChecked == true;
        draft.Layout = layouts[Math.Max(0, PanePosition.SelectedIndex)];
        if (CompletionTimeout.SelectedItem is int timeout) draft.IntelliSenseTimeoutSeconds = timeout;
        if (EditorFont.SelectedItem is string font) draft.FontFamily = font;
        if (EditorFontSize.SelectedItem is double size) draft.FontSize = size;
        ConsoleEnterSelects.IsEnabled = draft.ConsoleIntelliSense;
        ScriptEnterSelects.IsEnabled = draft.ScriptIntelliSense;
        var valid = int.TryParse(AutoSaveInterval.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 0 and <= 120;
        valid &= int.TryParse(RecentFileCount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var recent) && recent is >= 0 and <= 100;
        if (valid) { draft.AutoSaveMinutes = minutes; draft.RecentFileCount = recent; }
        ValidationPanel.IsVisible = !valid || !colorValid;
        ValidationMessage.Text = UiText.Get(colorValid ? "InvalidSettings" : "InvalidColor");
        AcceptOptions.IsEnabled = ApplyOptions.IsEnabled = valid && colorValid && !saving && JsonSerializer.Serialize(draft) != baseline;
        RefreshSample();
    }

    private void SelectColor()
    {
        colorKey = (ColorTree.SelectedItem as TreeViewItem)?.Tag as string;
        ColorControls.IsEnabled = colorKey is not null;
        colorValid = true;
        if (colorKey is not null) DisplayColor(Color.Parse(draft.Theme.Colors[colorKey]));
        else ColorSwatch.Color = null;
        UpdateDraft();
    }

    private void DisplayColor(Color color)
    {
        updating = true;
        string Channel(byte value) => value.ToString(Hexadecimal.IsChecked == true ? "X2" : "D", CultureInfo.InvariantCulture);
        RedValue.Text = Channel(color.R);
        GreenValue.Text = Channel(color.G);
        BlueValue.Text = Channel(color.B);
        RedSlider.Value = color.R; GreenSlider.Value = color.G; BlueSlider.Value = color.B;
        ColorSwatch.Color = color;
        NamedColor.SelectedItem = typeof(Colors).GetProperties().FirstOrDefault(p => p.PropertyType == typeof(Color) && (Color)p.GetValue(null)! == color)?.Name;
        updating = false;
    }

    private void ReadColor()
    {
        if (updating || colorKey is null) return;
        var style = Hexadecimal.IsChecked == true ? NumberStyles.AllowHexSpecifier : NumberStyles.None;
        colorValid = byte.TryParse(RedValue.Text, style, CultureInfo.InvariantCulture, out var red);
        colorValid &= byte.TryParse(GreenValue.Text, style, CultureInfo.InvariantCulture, out var green);
        colorValid &= byte.TryParse(BlueValue.Text, style, CultureInfo.InvariantCulture, out var blue);
        if (colorValid) SetColor(Color.FromRgb(red, green, blue));
        else UpdateDraft();
    }

    private void SetColor(Color color)
    {
        if (colorKey is null) return;
        colorValid = true;
        var value = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (draft.Theme.Colors[colorKey] != value)
        {
            draft.Theme.Colors[colorKey] = value;
            draft.Theme.Name = UiText.Get("CustomTheme");
        }
        DisplayColor(color);
        UpdateDraft();
    }

    private void RefreshSample()
    {
        if (SampleEditor is null) return;
        Width = 534 * DesktopTheme.TextScale;
        Height = 562 * DesktopTheme.TextScale;
        ThemeName.Text = draft.Theme.Name;
        SampleEditor.FontFamily = new FontFamily(draft.FontFamily + ", Consolas, DejaVu Sans Mono, monospace");
        SampleEditor.FontSize = draft.FontSize * 4 / 3;
        var pane = colorKey?.StartsWith("Console.") == true || colorKey?.StartsWith("Stream.") == true ? "Console" : "Script";
        SampleEditor.Background = DesktopTheme.HighContrast ? DesktopTheme.Brush("WindowBrush") : new SolidColorBrush(Color.Parse(draft.Theme.Colors[pane + ".Background"]));
        SampleEditor.Foreground = DesktopTheme.HighContrast ? DesktopTheme.Brush("WindowTextBrush") : new SolidColorBrush(Color.Parse(draft.Theme.Colors[pane + ".Foreground"]));
        sampleColorizer.Pane = pane;
        sampleColorizer.Theme = draft.Theme;
        SampleEditor.TextArea.TextView.Redraw();
        ColorSwatch.InvalidateVisual();
    }

    private void Restore()
    {
        var defaults = new UserSettings
        {
            RecentFiles = draft.RecentFiles, CustomThemes = draft.CustomThemes,
            LoadProfiles = draft.LoadProfiles, ShowCommands = draft.ShowCommands, WordWrap = draft.WordWrap, Zoom = draft.Zoom,
            HelpView = draft.HelpView.Copy(), DebuggerSessions = draft.DebuggerSessions, Geometry = draft.Geometry
        };
        draft = defaults;
        colorValid = true;
        LoadControls();
    }

    private async Task<bool> ApplyAsync()
    {
        UpdateDraft();
        if (!AcceptOptions.IsEnabled) return false;
        saving = true;
        CancelOptions.IsEnabled = RestoreDefaults.IsEnabled = ManageThemes.IsEnabled = false;
        ColorsPage.IsEnabled = GeneralPage.IsEnabled = false;
        UpdateDraft();
        try
        {
            var snapshot = draft.Copy();
            snapshot.Normalize();
            await apply(snapshot.Copy());
            draft = snapshot;
            baseline = JsonSerializer.Serialize(draft);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceError("Could not apply Options: {0}", exception);
            await Dialogs.ChooseAsync(this, UiText.Get("Options"), exception.Message, UiText.Get("OK"));
            return false;
        }
        finally
        {
            saving = false;
            CancelOptions.IsEnabled = RestoreDefaults.IsEnabled = ManageThemes.IsEnabled = true;
            ColorsPage.IsEnabled = GeneralPage.IsEnabled = true;
            UpdateDraft();
        }
    }

    private async Task ManageThemesAsync()
    {
        var themes = new List<EditorTheme> { new() { Name = UiText.Get("DefaultTheme") } };
        var light = new EditorTheme { Name = UiText.Get("LightTheme") };
        foreach (var (key, value) in light.Colors.ToArray())
        {
            if (key.StartsWith("Console.")) light.Colors[key] = light.Colors.GetValueOrDefault(key.Replace("Console.", "Script."), value);
        }
        light.Colors["Console.TextBackground"] = "#FFFFFF";
        light.Colors["Stream.Warning"] = light.Colors["Stream.Verbose"] = light.Colors["Stream.Debug"] = "#806000";
        themes.Add(light);
        var dark = new EditorTheme { Name = UiText.Get("DarkTheme") };
        foreach (var (key, value) in dark.Colors.ToArray())
            if (key.StartsWith("Script.")) dark.Colors[key] = dark.Colors.GetValueOrDefault(key.Replace("Script.", "Console."), value);
        themes.Add(dark);
        themes.AddRange(draft.CustomThemes);
        var manager = new Window
        {
            Title = UiText.Get("ManageThemes"), Width = 540, Height = 300, CanResize = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = AppIcon.Create()
        };
        manager.Classes.Add("options");
        var list = new ListBox { ItemsSource = themes.Select(t => t.Name).ToArray(), SelectedIndex = 0, Margin = new Thickness(10) };
        var panel = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        panel.Children.Add(list);
        var buttons = new WrapPanel { Margin = new Thickness(10), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        foreach (var key in new[] { "SaveTheme", "DeleteTheme", "ImportTheme", "ExportTheme", "OK", "Cancel" })
        {
            var button = new Button { Content = UiText.Get(key), Margin = new Thickness(3), IsCancel = key == "Cancel", IsDefault = key == "OK" };
            button.Click += async (_, _) =>
            {
                if (key is "ImportTheme" or "ExportTheme")
                {
                    try
                    {
                        var type = new FilePickerFileType(UiText.Get("ThemeFile")) { Patterns = ["*.json"] };
                        if (key == "ImportTheme")
                        {
                            var files = await manager.StorageProvider.OpenFilePickerAsync(new()
                            {
                                Title = UiText.Get(key), FileTypeFilter = [type]
                            });
                            if (files.Count == 0) return;
                            var imported = await ThemeFile.LoadAsync(files[0].TryGetLocalPath() ??
                                throw new IOException("Only local theme files are supported."));
                            if (themes.Take(3).Any(theme => theme.Name == imported.Theme.Name))
                                throw new InvalidDataException(UiText.Get("ThemeBuiltinName"));
                            if (draft.CustomThemes.Any(theme => theme.Name == imported.Theme.Name) &&
                                await Dialogs.ChooseAsync(manager, UiText.Get("ManageThemes"), UiText.Get("ThemeOverwrite"),
                                    UiText.Get("OK"), UiText.Get("Cancel")) != UiText.Get("OK")) return;
                            ApplyImportedTheme(imported);
                            themes.RemoveAll(theme => theme.Name == imported.Theme.Name);
                            themes.Add(imported.Theme.Copy());
                            list.ItemsSource = themes.Select(theme => theme.Name).ToArray();
                            list.SelectedIndex = themes.Count - 1;
                            LoadControls();
                        }
                        else
                        {
                            var file = await manager.StorageProvider.SaveFilePickerAsync(new()
                            {
                                Title = UiText.Get(key), SuggestedFileName = "theme.json", DefaultExtension = "json",
                                ShowOverwritePrompt = true, FileTypeChoices = [type]
                            });
                            if (file is null) return;
                            await ThemeFile.FromSettings(draft).SaveAsync(file.TryGetLocalPath() ??
                                throw new IOException("Only local theme files are supported."));
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                    {
                        System.Diagnostics.Trace.TraceError("Theme transfer failed: {0}", exception);
                        await Dialogs.ChooseAsync(manager, UiText.Get("ManageThemes"), exception.Message, UiText.Get("OK"));
                    }
                    return;
                }
                if (key == "Cancel") { manager.Close(); return; }
                if (key == "SaveTheme")
                {
                    var name = await Dialogs.AskAsync(manager, UiText.Get("ManageThemes"), UiText.Get("ThemeName"));
                    if (name is null) return;
                    if (string.IsNullOrWhiteSpace(name) || themes.Take(3).Any(t => t.Name == name.Trim()))
                    {
                        await Dialogs.ChooseAsync(manager, UiText.Get("ManageThemes"), "Enter a non-empty name other than a built-in theme.", UiText.Get("OK"));
                        return;
                    }
                    var saved = draft.Theme.Copy(); saved.Name = name.Trim();
                    draft.CustomThemes.RemoveAll(t => t.Name == saved.Name);
                    draft.CustomThemes.Add(saved);
                    manager.Close();
                    draft.Theme = saved.Copy();
                }
                else if (list.SelectedIndex >= 0)
                {
                    var selected = themes[list.SelectedIndex];
                    if (key == "OK") { draft.Theme = selected.Copy(); manager.Close(); }
                    else if (list.SelectedIndex >= 3)
                    {
                        draft.CustomThemes.RemoveAll(t => t.Name == selected.Name);
                        themes.RemoveAt(list.SelectedIndex);
                        list.ItemsSource = themes.Select(t => t.Name).ToArray();
                        list.SelectedIndex = 0;
                    }
                }
                colorValid = true;
                LoadControls();
            };
            buttons.Children.Add(button);
        }
        Grid.SetRow(buttons, 1);
        panel.Children.Add(buttons);
        manager.Content = panel;
        await manager.ShowDialog(this);
        UpdateDraft();
    }

    public void ApplyImportedTheme(ThemeFile imported)
    {
        imported.Validate();
        updating = true;
        draft.Theme = imported.Theme.Copy();
        draft.FontFamily = imported.FontFamily;
        draft.FontSize = imported.FontSize;
        EditorFontSize.ItemsSource = ((IEnumerable<double>)EditorFontSize.ItemsSource!).Append(imported.FontSize).Distinct().Order().ToArray();
        draft.CustomThemes.RemoveAll(theme => theme.Name == imported.Theme.Name);
        draft.CustomThemes.Add(imported.Theme.Copy());
        colorValid = true;
        LoadControls();
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        OptionsTabs.SelectedIndex = 1 - OptionsTabs.SelectedIndex;
        OptionsTabs.Focus();
        e.Handled = true;
    }
}
