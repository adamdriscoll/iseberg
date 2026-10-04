using System.Management.Automation;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Search;
using Iseberg.Core;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg;

public sealed partial class MainWindow : Window
{
    private readonly string[] startupFiles;
    private readonly DispatcherTimer outputTimer;
    private readonly DispatcherTimer analysisTimer;
    private readonly PowerShellColorizer colorizer = new();
    private FoldingManager folding;
    private readonly SearchPanel search;
    private UserSettings settings = new();
    private SessionModel? displayedSession;
    private ScriptTab? displayedFile;
    private CompletionWindow? completion;
    private int fileNumber;
    private int sessionNumber;
    private bool initialized;
    private bool closingApproved;
    private bool closingInProgress;
    private int commandRequestVersion;
    private bool consolePromptStacked;
    public WorkbenchModel Workbench { get; } = new();

    public MainWindow() : this([]) { }

    public MainWindow(string[] args, bool initializeOnOpen = true)
    {
        startupFiles = args;
        InitializeComponent();
        DataContext = Workbench;
        ScriptEditor.Options.IndentationSize = 4;
        ScriptEditor.Options.ConvertTabsToSpaces = true;
        ScriptEditor.Options.HighlightCurrentLine = true;
        ScriptEditor.TextArea.TextView.LineTransformers.Add(colorizer);
        ScriptEditor.TextArea.TextView.BackgroundRenderers.Add(new ScriptAdornments(
            () => displayedFile, () => displayedSession?.DebugLocation));
        ConsoleOutput.TextArea.TextView.LineTransformers.Add(new ConsoleColorizer(() => displayedSession));
        ConsoleOutput.Options.AllowScrollBelowDocument = false;
        folding = FoldingManager.Install(ScriptEditor.TextArea);
        search = SearchPanel.Install(ScriptEditor);
        outputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        outputTimer.Tick += (_, _) => FlushOutput();
        analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        analysisTimer.Tick += (_, _) => { analysisTimer.Stop(); AnalyzeScript(); };
        ScriptEditor.TextChanged += (_, _) => { analysisTimer.Stop(); analysisTimer.Start(); RefreshCaret(); };
        ScriptEditor.TextArea.Caret.PositionChanged += (_, _) => RefreshCaret();
        ScriptEditor.TextArea.TextEntered += async (_, e) =>
        {
            if (e.Text is "$" or "-" or ".")
                await GuardAsync(ShowCompletionAsync);
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        ConsoleInput.AddHandler(KeyDownEvent, OnConsoleKeyDown, RoutingStrategies.Tunnel);
        ConsolePane.SizeChanged += (_, _) => SizeConsoleOutput();
        if (initializeOnOpen && !Design.IsDesignMode)
        {
            Opened += async (_, _) => await GuardAsync(StartAsync);
            Closing += OnClosing;
        }
        Closed += (_, _) => { outputTimer.Stop(); analysisTimer.Stop(); };
    }

    private async Task StartAsync()
    {
        try { settings = await UserSettings.LoadAsync(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            await ReportErrorAsync("Could not load settings", exception);
        }
        initialized = true;
        ApplySettings();
        PopulateRecentMenu();
        await NewSessionAsync();
        foreach (var path in startupFiles)
            if (!path.StartsWith("--", StringComparison.Ordinal))
                await OpenFileAsync(path);
        outputTimer.Start();
        ScriptEditor.Focus();
    }

    private async Task NewSessionAsync()
    {
        var session = new SessionModel($"PowerShell {++sessionNumber}");
        session.Engine.StateChanged += _ => Dispatcher.UIThread.Post(RefreshState);
        session.Engine.InputRequested += request => Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                Workbench.SelectedSession = session;
                DisplaySession();
                var answer = await Dialogs.AskAsync(this, request.Caption, request.Message, secret: request.Secret);
                if (answer is null) request.Response.TrySetCanceled();
                else request.Response.TrySetResult(answer);
            }
            catch (Exception exception) when (exception is InvalidOperationException)
            {
                request.Response.TrySetException(exception);
                await ReportErrorAsync("PowerShell input failed", exception);
            }
        });
        session.Engine.ConsoleCleared += () => Dispatcher.UIThread.Post(() => session.ClearOutput());
        session.Engine.ProgressChanged += progress => Dispatcher.UIThread.Post(() =>
        {
            if (session != displayedSession) return;
            ProgressPanel.IsVisible = !progress.Completed;
            ProgressText.Text = progress.Activity + " - " + progress.Status;
            ScriptProgress.IsIndeterminate = progress.Percent < 0;
            ScriptProgress.Value = Math.Max(0, progress.Percent);
        });
        session.Engine.DebuggerStopped += location => Dispatcher.UIThread.Post(async () =>
        {
            session.DebugLocation = location;
            if (location is not null)
            {
                Workbench.SelectedSession = session;
                DisplaySession();
                if (!string.IsNullOrEmpty(location.ScriptPath) && File.Exists(location.ScriptPath))
                    await GuardAsync(async () => await OpenFileAsync(location.ScriptPath));
                if (displayedFile is not null && location.Line > 0 && location.Line <= ScriptEditor.Document.LineCount)
                {
                    ScriptEditor.ScrollToLine(location.Line);
                    ScriptEditor.TextArea.Caret.Line = location.Line;
                }
            }
            ScriptEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
            RefreshState();
        });
        Workbench.Sessions.Add(session);
        Workbench.SelectedSession = session;
        NewFile();
        DisplaySession();
        try
        {
            await session.Engine.InitializeAsync();
            session.ConsoleDocument.Text = $"PowerShell {session.Engine.Version}\nCopyright (c) Microsoft Corporation.\n\n";
            if (settings.LoadProfiles) await LoadProfilesAsync(session);
            await RefreshCommandsAsync(session);
        }
        catch (Exception exception) when (exception is RuntimeException or InvalidOperationException or IOException)
        {
            session.ConsoleDocument.Insert(session.ConsoleDocument.TextLength, $"Session initialization failed: {exception.Message}\n");
            await ReportErrorAsync("Could not initialize PowerShell", exception);
        }
        RefreshState();
        SizeConsoleOutput();
    }

    private void SizeConsoleOutput()
    {
        var stacked = (PromptText.Text?.Length ?? 0) * ConsoleOutput.TextArea.TextView.WideSpaceWidth > ConsolePane.Bounds.Width - 120;
        if (stacked != consolePromptStacked)
        {
            consolePromptStacked = stacked;
            PromptInputPanel.ColumnDefinitions = new(stacked ? "*" : "Auto,*");
            PromptInputPanel.RowDefinitions = new(stacked ? "Auto,Auto" : "Auto");
            Grid.SetColumn(ConsoleInput, stacked ? 0 : 1);
            Grid.SetRow(ConsoleInput, stacked ? 1 : 0);
            PromptText.TextWrapping = stacked ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }
        var available = Math.Max(30, ConsolePane.Bounds.Height - Math.Max(34, PromptInputPanel.Bounds.Height + 8) - (ProgressPanel.IsVisible ? 45 : 0));
        var height = Math.Min(available, Math.Max(ConsoleOutput.FontSize * 1.3, ConsoleOutput.TextArea.TextView.DocumentHeight + 4));
        if (Math.Abs(ConsoleOutput.Height - height) > 0.5)
        {
            ConsoleOutput.Height = height;
            Dispatcher.UIThread.Post(ConsoleOutput.ScrollToEnd, DispatcherPriority.Background);
        }
    }

    private void NewFile()
    {
        if (Workbench.SelectedSession is not { } session) return;
        var file = new ScriptTab(new ScriptFile($"Untitled{++fileNumber}.ps1"));
        session.Files.Add(file);
        session.SelectedFile = file;
        DisplayFile();
    }

    private void OnSessionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source == SessionTabs) DisplaySession();
    }

    private void OnFileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source != FileTabs) return;
        if (Workbench.SelectedSession is { } session && FileTabs.SelectedItem is ScriptTab file &&
            session.Files.Contains(file) && session.SelectedFile != file)
            session.SelectedFile = file;
        DisplayFile();
    }

    private void DisplaySession()
    {
        var next = Workbench.SelectedSession;
        if (displayedSession == next) return;
        if (displayedSession is not null)
            displayedSession.Input = ConsoleInput.Text ?? "";
        displayedSession = next;
        if (next is null) return;
        ConsoleOutput.Document = next.ConsoleDocument;
        ConsoleInput.Text = next.Input;
        ProgressPanel.IsVisible = false;
        DisplayFile();
        SetCommandModules();
        RefreshState();
        ConsoleOutput.ScrollToEnd();
    }

    private void DisplayFile()
    {
        var next = Workbench.SelectedSession?.SelectedFile;
        if (next == displayedFile) return;
        if (displayedFile is not null) displayedFile.File.CaretOffset = ScriptEditor.CaretOffset;
        displayedFile = next;
        completion?.Close();
        FoldingManager.Uninstall(folding);
        ScriptEditor.Document = next?.Document ?? new TextDocument();
        folding = FoldingManager.Install(ScriptEditor.TextArea);
        ScriptEditor.CaretOffset = Math.Min(next?.File.CaretOffset ?? 0, ScriptEditor.Document.TextLength);
        AnalyzeScript();
        RefreshCaret();
        RefreshState();
    }

    private void FlushOutput()
    {
        foreach (var session in Workbench.Sessions)
        {
            if (session.FlushOutput() && session == displayedSession)
            {
                ConsoleOutput.TextArea.TextView.Redraw();
                ConsoleOutput.ScrollToEnd();
            }
        }
        RefreshState();
        SizeConsoleOutput();
    }

    private void AnalyzeScript()
    {
        colorizer.Analysis = EditorAnalysis.Analyze(ScriptEditor.Text);
        ScriptEditor.TextArea.TextView.Redraw();
        folding.UpdateFoldings(colorizer.Analysis.Folds.Select(f => new NewFolding(f.Start, f.End)), -1);
        Diagnostics.IsVisible = colorizer.Analysis.Errors.Length > 0;
        Diagnostics.Text = string.Join("  |  ", colorizer.Analysis.Errors.Take(3).Select(
            error => $"Line {error.Extent.StartLineNumber}: {error.Message}"));
    }

    private void RefreshCaret()
    {
        CaretText.Text = $"Ln {ScriptEditor.TextArea.Caret.Line}  Col {ScriptEditor.TextArea.Caret.Column}";
        EncodingText.Text = displayedFile?.File.EncodingName ?? "";
        Title = $"{displayedFile?.File.Title ?? "Iseberg"} - Iseberg - PowerShell 7 ISE";
    }

    private void RefreshState()
    {
        var state = displayedSession?.Engine.State ?? SessionState.Starting;
        var ready = state == SessionState.Ready;
        var paused = state == SessionState.Debugging;
        RunButton.IsEnabled = displayedFile is not null && (ready || paused);
        SelectionButton.IsEnabled = displayedFile is not null && ready;
        StopButton.IsEnabled = state is SessionState.Running or SessionState.Debugging;
        CommandRunButton.IsEnabled = ready && CommandList.SelectedItem is not null;
        ConsoleInput.IsEnabled = ready;
        ScriptEditor.IsReadOnly = paused;
        PromptText.Text = displayedSession?.Engine.Prompt ?? "PS> ";
        StatusText.Text = state switch
        {
            SessionState.Ready => "Ready",
            SessionState.Running => "Running script / selection. Press Ctrl+Break to stop.",
            SessionState.Debugging => $"Breakpoint at line {displayedSession?.DebugLocation?.Line}. F5: Continue  F10: Step over  F11: Step into",
            SessionState.Disposed => "Session closed",
            _ => "Starting PowerShell..."
        };
        RefreshCaret();
    }

    private async void OnAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string action })
            await GuardAsync(() => ActAsync(action));
    }

    private async Task ActAsync(string action)
    {
        var session = Workbench.SelectedSession;
        switch (action)
        {
            case "New": NewFile(); break;
            case "Open": await PickFilesAsync(); break;
            case "Save": if (displayedFile is not null) await SaveFileAsync(displayedFile); break;
            case "SaveAs": if (displayedFile is not null) await SaveFileAsync(displayedFile, true); break;
            case "SaveAll":
                foreach (var tab in Workbench.Sessions.SelectMany(s => s.Files).ToArray())
                    if (tab.File.IsDirty && !await SaveFileAsync(tab)) break;
                break;
            case "Close": if (session is not null && displayedFile is not null) await CloseFileAsync(session, displayedFile); break;
            case "NewSession": await NewSessionAsync(); break;
            case "CloseSession": if (session is not null) await CloseSessionAsync(session); break;
            case "Exit": Close(); break;
            case "Undo": ScriptEditor.Undo(); break;
            case "Redo": ScriptEditor.Redo(); break;
            case "Cut": ScriptEditor.Cut(); break;
            case "Copy": if (ConsoleInput.IsFocused) ConsoleInput.Copy(); else if (ConsoleOutput.IsKeyboardFocusWithin) ConsoleOutput.Copy(); else ScriptEditor.Copy(); break;
            case "Paste": if (ConsoleInput.IsFocused) ConsoleInput.Paste(); else ScriptEditor.Paste(); break;
            case "SelectAll": if (ConsoleInput.IsFocused) ConsoleInput.SelectAll(); else if (ConsoleOutput.IsKeyboardFocusWithin) ConsoleOutput.SelectAll(); else ScriptEditor.SelectAll(); break;
            case "Find": search.Open(); search.Reactivate(); break;
            case "Replace": await ReplaceAsync(); break;
            case "GoToLine": await GoToLineAsync(); break;
            case "Run": if (session?.Engine.State == SessionState.Debugging) session.Engine.Resume(DebuggerResumeAction.Continue); else await RunScriptAsync(false); break;
            case "RunSelection": await RunScriptAsync(true); break;
            case "Stop": if (session is not null) await session.Engine.StopAsync(); break;
            case "Continue": session?.Engine.Resume(DebuggerResumeAction.Continue); break;
            case "StepInto": session?.Engine.Resume(DebuggerResumeAction.StepInto); break;
            case "StepOver": session?.Engine.Resume(DebuggerResumeAction.StepOver); break;
            case "StepOut": session?.Engine.Resume(DebuggerResumeAction.StepOut); break;
            case "Breakpoint":
                if (session?.Engine.State != SessionState.Ready) throw new InvalidOperationException("Wait for execution to finish before changing breakpoints.");
                if (displayedFile is not null)
                {
                    var line = ScriptEditor.TextArea.Caret.Line;
                    displayedFile.ToggleBreakpoint(line);
                    ScriptEditor.TextArea.TextView.Redraw();
                }
                break;
            case "RemoveBreakpoints":
                if (session?.Engine.State != SessionState.Ready) throw new InvalidOperationException("Wait for execution to finish before changing breakpoints.");
                if (session is not null)
                    foreach (var file in session.Files)
                    {
                        file.ClearBreakpoints();
                        if (file.File.Path is not null) await session.Engine.SetBreakpointsAsync(file.File.Path, []);
                    }
                ScriptEditor.TextArea.TextView.Redraw();
                break;
            case "Complete": await ShowCompletionAsync(); break;
            case "Snippets": await InsertSnippetAsync(); break;
            case "Clear": session?.ClearOutput(); break;
            case "Top": case "Right": case "Maximized": settings.Layout = action; ApplySettings(); break;
            case "Commands": settings.ShowCommands = !settings.ShowCommands; ApplySettings(); break;
            case "LineNumbers": settings.ShowLineNumbers = !settings.ShowLineNumbers; ApplySettings(); break;
            case "WordWrap": settings.WordWrap = !settings.WordWrap; ApplySettings(); break;
            case "ZoomIn": ZoomSlider.Value = Math.Min(400, ZoomSlider.Value + 5); break;
            case "ZoomOut": ZoomSlider.Value = Math.Max(20, ZoomSlider.Value - 5); break;
            case "Fold":
                var collapse = folding.AllFoldings.Any(f => !f.IsFolded);
                foreach (var fold in folding.AllFoldings) fold.IsFolded = collapse;
                break;
            case "FocusScript": ScriptEditor.Focus(); break;
            case "FocusConsole": if (settings.Layout == "Maximized") { settings.Layout = "Top"; ApplySettings(); } ConsoleInput.Focus(); break;
            case "RefreshCommands": if (session is not null) await RefreshCommandsAsync(session); break;
            case "InsertCommand": InsertCommand(); break;
            case "RunCommand": if (session is not null && CommandList.SelectedItem is CommandDescription command) await session.Engine.ExecuteAsync(command.Name); break;
            case "CommandHelp": if (CommandList.SelectedItem is CommandDescription selected) await ShowHelpAsync(selected.Name); break;
            case "Help": await ShowHelpAsync(ScriptEditor.SelectedText.Length > 0 ? ScriptEditor.SelectedText : CommandAtCaret()); break;
            case "Profiles": if (session is not null) await LoadProfilesAsync(session); break;
            case "ExecutionPolicy":
                if (session is not null && OperatingSystem.IsWindows() &&
                    await Dialogs.ChooseAsync(this, "Execution policy",
                        "Set RemoteSigned for this application process? Local scripts can run; downloaded scripts still require a trusted signature. Machine and user settings are not changed, and Group Policy still applies.",
                        "Enable", "Cancel") == "Enable")
                    await session.Engine.ExecuteAsync("Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force");
                break;
            case "Options": await OptionsAsync(); break;
            case "About":
                await Dialogs.ShowTextAsync(this, "About Iseberg",
                    $"Iseberg\nA cross-platform PowerShell ISE-style editor and terminal.\n\nPowerShell {session?.Engine.Version}\nAvalonia + AvaloniaEdit + PowerShell SDK\n\nSee README.md and docs/parity.md for implemented behavior and known differences.");
                break;
        }
    }

    private async Task PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Open script",
            AllowMultiple = true,
            FileTypeFilter = [new("PowerShell scripts") { Patterns = ["*.ps1", "*.psm1", "*.psd1"] }, FilePickerFileTypes.All]
        });
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath() ?? throw new IOException("Only local script files are supported.");
            await OpenFileAsync(path);
        }
    }

    public async Task OpenFileAsync(string path)
    {
        if (Workbench.SelectedSession is not { } session) return;
        var fullPath = Path.GetFullPath(path);
        var existing = session.Files.FirstOrDefault(tab => string.Equals(tab.File.Path, fullPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var tab = existing ?? new ScriptTab(await ScriptFile.OpenAsync(fullPath));
        if (existing is null) session.Files.Add(tab);
        session.SelectedFile = tab;
        DisplayFile();
        RememberFile(fullPath);
    }

    private async Task<bool> SaveFileAsync(ScriptTab tab, bool saveAs = false)
    {
        var path = tab.File.Path;
        if (path is null || saveAs)
        {
            var result = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Save script",
                SuggestedFileName = tab.File.Name,
                DefaultExtension = "ps1",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new("PowerShell script") { Patterns = ["*.ps1"] }, FilePickerFileTypes.All]
            });
            if (result is null) return false;
            path = result.TryGetLocalPath() ?? throw new IOException("Only local script files are supported.");
        }
        var previousPath = tab.File.Path;
        await tab.File.SaveAsync(path);
        if (saveAs && previousPath is not null && previousPath != tab.File.Path)
        {
            var owner = Workbench.Sessions.First(s => s.Files.Contains(tab));
            if (owner.Engine.State == SessionState.Ready) await owner.Engine.SetBreakpointsAsync(previousPath, []);
        }
        RememberFile(path);
        RefreshCaret();
        return true;
    }

    private void RememberFile(string path)
    {
        settings.RecentFiles.RemoveAll(p => string.Equals(p, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        settings.RecentFiles.Insert(0, path);
        if (settings.RecentFiles.Count > 10) settings.RecentFiles.RemoveRange(10, settings.RecentFiles.Count - 10);
        PopulateRecentMenu();
    }

    private void PopulateRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var recent in settings.RecentFiles)
        {
            var item = new MenuItem { Header = recent.Replace("_", "__") };
            item.Click += async (_, _) => await GuardAsync(() => OpenFileAsync(recent));
            RecentMenu.Items.Add(item);
        }
    }

    private async Task<bool> ConfirmSaveAsync(ScriptTab tab)
    {
        if (!tab.File.IsDirty) return true;
        var result = await Dialogs.ChooseAsync(this, "Save changes", $"Save changes to {tab.File.Name}?", "Save", "Don't Save", "Cancel");
        return result == "Don't Save" || (result == "Save" && await SaveFileAsync(tab));
    }

    private async Task CloseFileAsync(SessionModel session, ScriptTab tab)
    {
        if (session.Engine.State is SessionState.Running or SessionState.Debugging)
            throw new InvalidOperationException("Stop execution before closing a script.");
        if (!await ConfirmSaveAsync(tab)) return;
        if (tab.File.Path is not null) await session.Engine.SetBreakpointsAsync(tab.File.Path, []);
        session.Files.Remove(tab);
        if (session.SelectedFile == tab) session.SelectedFile = session.Files.LastOrDefault();
        if (session.Files.Count == 0) NewFile();
        DisplayFile();
    }

    private async void OnCloseFile(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: ScriptTab tab } && Workbench.SelectedSession is { } session)
            await GuardAsync(() => CloseFileAsync(session, tab));
    }

    private async void OnCloseSession(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: SessionModel session })
            await GuardAsync(async () => await CloseSessionAsync(session));
    }

    private async Task<bool> CloseSessionAsync(SessionModel session)
    {
        if (session.Engine.State is SessionState.Running or SessionState.Debugging)
        {
            if (await Dialogs.ChooseAsync(this, "Stop execution", "Stop the running command and close this PowerShell tab?", "Stop", "Cancel") != "Stop")
                return false;
            await session.Engine.StopAsync();
        }
        foreach (var file in session.Files)
            if (!await ConfirmSaveAsync(file)) return false;
        await session.Engine.DisposeAsync();
        Workbench.Sessions.Remove(session);
        if (Workbench.SelectedSession == session) Workbench.SelectedSession = Workbench.Sessions.LastOrDefault();
        if (Workbench.Sessions.Count == 0 && !closingInProgress) await NewSessionAsync();
        DisplaySession();
        return true;
    }

    private async Task RunScriptAsync(bool selection)
    {
        var session = displayedSession;
        var file = displayedFile;
        if (session is null || file is null) return;
        if (selection)
        {
            var text = ScriptEditor.SelectedText;
            if (text.Length == 0) text = ScriptEditor.Document.GetText(ScriptEditor.Document.GetLineByNumber(ScriptEditor.TextArea.Caret.Line));
            await session.Engine.ExecuteAsync(text);
        }
        else
        {
            if (file.File.Path is not null || file.File.Breakpoints.Count > 0)
            {
                if ((file.File.IsDirty || file.File.Path is null) && !await SaveFileAsync(file)) return;
                foreach (var tab in session.Files.Where(tab => tab.File.Path is not null))
                    await session.Engine.SetBreakpointsAsync(tab.File.Path!, tab.File.Breakpoints);
            }
            await session.Engine.ExecuteAsync(file.File.Text, file.File.Path);
        }
        FlushOutput();
        RefreshState();
    }

    private async void OnConsoleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (displayedSession is not { } session) return;
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var text = ConsoleInput.Text ?? "";
            if (EditorAnalysis.Analyze(text).Errors.Any(error => error.IncompleteInput))
                return;
            e.Handled = true;
            if (string.IsNullOrWhiteSpace(text)) return;
            ConsoleInput.Text = "";
            if (session.History.Count == 0 || session.History[^1] != text) session.History.Add(text);
            if (session.History.Count > 1000) session.History.RemoveAt(0);
            session.HistoryIndex = session.History.Count;
            session.DraftInput = "";
            await GuardAsync(async () => { await session.Engine.ExecuteAsync(text); FlushOutput(); ConsoleInput.Focus(); });
        }
        else if (e.Key is Key.Up or Key.Down && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !(ConsoleInput.Text ?? "").Contains('\n'))
        {
            e.Handled = true;
            if (session.HistoryIndex == session.History.Count) session.DraftInput = ConsoleInput.Text ?? "";
            session.HistoryIndex = Math.Clamp(session.HistoryIndex + (e.Key == Key.Up ? -1 : 1), 0, session.History.Count);
            ConsoleInput.Text = session.HistoryIndex == session.History.Count ? session.DraftInput : session.History[session.HistoryIndex];
            ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
        }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            await GuardAsync(() => CompleteConsoleAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
        }
    }

    private async Task CompleteConsoleAsync(bool backwards = false)
    {
        if (displayedSession is not { } session) return;
        var text = ConsoleInput.Text ?? "";
        var caret = ConsoleInput.CaretIndex;
        var previous = session.Completion;
        var cycling = previous is not null && previous.LastText == text && previous.LastCaret == caret;
        var matches = cycling ? previous!.Results : await session.Engine.CompleteAsync(text, caret);
        if (session != displayedSession || text != ConsoleInput.Text || caret != ConsoleInput.CaretIndex || matches.Matches.Count == 0) return;
        var index = cycling ? (previous!.Index + (backwards ? -1 : 1) + matches.Matches.Count) % matches.Matches.Count : (backwards ? matches.Matches.Count - 1 : 0);
        var original = cycling ? previous!.Original : text;
        var match = matches.Matches[index];
        ConsoleInput.Text = original.Remove(matches.Start, matches.Length).Insert(matches.Start, match.CompletionText);
        ConsoleInput.CaretIndex = matches.Start + match.CompletionText.Length;
        session.Completion = new(original, ConsoleInput.Text, ConsoleInput.CaretIndex, index, matches);
    }

    private async Task ShowCompletionAsync()
    {
        if (ConsoleInput.IsFocused) { await CompleteConsoleAsync(); return; }
        if (displayedSession is not { Engine.State: SessionState.Ready } session || completion is not null) return;
        var document = ScriptEditor.Document;
        var text = document.Text;
        var caret = ScriptEditor.CaretOffset;
        var results = await session.Engine.CompleteAsync(text, caret);
        if (displayedSession != session || ScriptEditor.Document != document || document.Text != text || ScriptEditor.CaretOffset != caret || results.Matches.Count == 0) return;
        completion = new CompletionWindow(ScriptEditor.TextArea)
        {
            StartOffset = results.Start,
            EndOffset = results.Start + results.Length
        };
        foreach (var match in results.Matches.Take(300)) completion.CompletionList.CompletionData.Add(new PowerShellCompletion(match));
        completion.Closed += (_, _) => completion = null;
        completion.Show();
    }

    private void ApplySettings()
    {
        ScriptEditor.ShowLineNumbers = settings.ShowLineNumbers;
        ScriptEditor.WordWrap = settings.WordWrap;
        ZoomSlider.Value = settings.Zoom;
        SetZoom(settings.Zoom);
        CommandsPane.IsVisible = CommandSplitter.IsVisible = settings.ShowCommands;
        OuterGrid.ColumnDefinitions = new(settings.ShowCommands ? "*,5,290" : "*,0,0");
        var right = settings.Layout == "Right";
        var maximized = settings.Layout == "Maximized";
        PaneGrid.RowDefinitions = new(right || maximized ? "*" : "3*,5,2*");
        PaneGrid.ColumnDefinitions = new(right ? "2*,5,3*" : "*");
        Grid.SetRow(ScriptPane, 0);
        Grid.SetColumn(ScriptPane, right ? 2 : 0);
        Grid.SetRow(ConsolePane, right ? 0 : 2);
        Grid.SetColumn(ConsolePane, 0);
        Grid.SetRow(PaneSplitter, right ? 0 : 1);
        Grid.SetColumn(PaneSplitter, right ? 1 : 0);
        PaneSplitter.Width = right ? 5 : double.NaN;
        PaneSplitter.Height = right ? double.NaN : 5;
        PaneSplitter.ResizeDirection = right ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        PaneSplitter.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        ConsolePane.IsVisible = PaneSplitter.IsVisible = !maximized;
    }

    private void OnZoomChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!initialized) return;
        settings.Zoom = e.NewValue;
        SetZoom(e.NewValue);
    }

    private void SetZoom(double percent)
    {
        ScriptEditor.FontSize = ConsoleOutput.FontSize = ConsoleInput.FontSize = PromptText.FontSize = 16 * percent / 100;
        ZoomText.Text = $"{percent:0}%";
    }

    private async Task RefreshCommandsAsync(SessionModel session)
    {
        var version = ++commandRequestVersion;
        var commands = await session.Engine.GetCommandsAsync();
        session.Commands = commands;
        if (session == displayedSession && version == commandRequestVersion) SetCommandModules();
    }

    private void SetCommandModules()
    {
        ModuleFilter.ItemsSource = new[] { "All" }.Concat((displayedSession?.Commands ?? [])
            .Select(c => c.Module).Where(m => m.Length > 0).Distinct().Order());
        ModuleFilter.SelectedIndex = 0;
        FilterCommands();
    }

    private void FilterCommands()
    {
        if (CommandList is null) return;
        var name = CommandSearch.Text ?? "";
        var module = ModuleFilter.SelectedItem as string;
        CommandList.ItemsSource = displayedSession?.Commands.Where(c =>
            c.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && (module is null or "All" || c.Module == module)).ToArray();
    }

    private void OnCommandFilterChanged(object? sender, SelectionChangedEventArgs e) => FilterCommands();
    private void OnCommandSearchChanged(object? sender, TextChangedEventArgs e) => FilterCommands();
    private void OnCommandSelected(object? sender, SelectionChangedEventArgs e)
    {
        CommandSyntax.Text = (CommandList.SelectedItem as CommandDescription)?.Definition ?? "";
        RefreshState();
    }
    private void OnInsertCommand(object? sender, TappedEventArgs e) => InsertCommand();
    private void InsertCommand()
    {
        if (CommandList.SelectedItem is not CommandDescription command || ScriptEditor.IsReadOnly) return;
        ScriptEditor.Document.Replace(ScriptEditor.SelectionStart, ScriptEditor.SelectionLength, command.Name + " ");
        ScriptEditor.Focus();
    }

    private string CommandAtCaret()
    {
        var offset = ScriptEditor.CaretOffset;
        var token = colorizer.Analysis?.Tokens.FirstOrDefault(t => t.Extent.StartOffset <= offset && t.Extent.EndOffset >= offset);
        return token?.Text ?? "Get-Help";
    }

    private async Task ShowHelpAsync(string command)
    {
        if (displayedSession is null) return;
        var text = await displayedSession.Engine.GetHelpAsync(command);
        await Dialogs.ShowTextAsync(this, "PowerShell help - " + command, text);
    }

    private async Task ReplaceAsync()
    {
        if (ScriptEditor.IsReadOnly) throw new InvalidOperationException("Resume or stop debugging before replacing script text.");
        var find = await Dialogs.AskAsync(this, "Replace", "Find what:", ScriptEditor.SelectedText);
        if (string.IsNullOrEmpty(find)) return;
        var replace = await Dialogs.AskAsync(this, "Replace", "Replace with:");
        if (replace is null) return;
        var choice = await Dialogs.ChooseAsync(this, "Replace", "Search is case-insensitive and literal.", "Replace Next", "Replace All", "Cancel");
        if (choice == "Replace All")
            ScriptEditor.Document.Text = ScriptEditor.Text.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
        else if (choice == "Replace Next")
        {
            var offset = ScriptEditor.Text.IndexOf(find, ScriptEditor.CaretOffset, StringComparison.OrdinalIgnoreCase);
            if (offset < 0) offset = ScriptEditor.Text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            if (offset >= 0) { ScriptEditor.Document.Replace(offset, find.Length, replace); ScriptEditor.Select(offset, replace.Length); }
        }
    }

    private async Task GoToLineAsync()
    {
        var input = await Dialogs.AskAsync(this, "Go to line", $"Line number (1 - {ScriptEditor.Document.LineCount}):");
        if (input is null) return;
        if (!int.TryParse(input, out var line) || line < 1 || line > ScriptEditor.Document.LineCount)
            throw new InvalidOperationException("Enter a valid line number.");
        ScriptEditor.TextArea.Caret.Line = line;
        ScriptEditor.ScrollToLine(line);
        ScriptEditor.Focus();
    }

    private async Task InsertSnippetAsync()
    {
        var choice = await Dialogs.ChooseAsync(this, "Snippets", "Insert a PowerShell structure at the caret:", "Function", "ForEach", "Try / Catch", "Cancel");
        var text = choice switch
        {
            "Function" => "function Verb-Noun {\n    [CmdletBinding()]\n    param()\n\n    \n}\n",
            "ForEach" => "foreach ($item in $collection) {\n    \n}\n",
            "Try / Catch" => "try {\n    \n}\ncatch {\n    Write-Error $_\n}\n",
            _ => null
        };
        if (text is not null && !ScriptEditor.IsReadOnly) ScriptEditor.Document.Replace(ScriptEditor.SelectionStart, ScriptEditor.SelectionLength, text);
    }

    private async Task LoadProfilesAsync(SessionModel session)
    {
        await session.Engine.ExecuteAsync("""
            foreach ($profilePath in @($PROFILE.AllUsersAllHosts, $PROFILE.AllUsersCurrentHost, $PROFILE.CurrentUserAllHosts, $PROFILE.CurrentUserCurrentHost)) {
                if (Test-Path -LiteralPath $profilePath) { . $profilePath }
            }
            """);
    }

    private async Task OptionsAsync()
    {
        var choice = await Dialogs.ChooseAsync(this, "Options",
            $"Zoom: {settings.Zoom:0}%\nLayout: {settings.Layout}\nLoad profiles in new tabs: {settings.LoadProfiles}\n\nPreferences are stored in:\n{UserSettings.SettingsPath}",
            "Toggle Profiles", "Reset View", "Close");
        if (choice == "Toggle Profiles") settings.LoadProfiles = !settings.LoadProfiles;
        if (choice == "Reset View")
        {
            settings.Zoom = 100; settings.Layout = "Top"; settings.ShowCommands = settings.ShowLineNumbers = true; settings.WordWrap = false;
            ApplySettings();
        }
        await settings.SaveAsync();
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        string? action = e.Key switch
        {
            Key.F5 => shift ? "Stop" : "Run",
            Key.F8 => "RunSelection",
            Key.F9 => ctrl && shift ? "RemoveBreakpoints" : "Breakpoint",
            Key.F10 => "StepOver",
            Key.F11 => shift ? "StepOut" : "StepInto",
            Key.F1 => "Help",
            Key.Pause when ctrl => "Stop",
            Key.N when ctrl => "New",
            Key.O when ctrl => "Open",
            Key.S when ctrl => shift ? "SaveAs" : "Save",
            Key.W when ctrl => shift ? "CloseSession" : "Close",
            Key.T when ctrl => "NewSession",
            Key.F when ctrl => "Find",
            Key.H when ctrl => "Replace",
            Key.G when ctrl => "GoToLine",
            Key.J when ctrl => "Snippets",
            Key.L when ctrl => "Clear",
            Key.I when ctrl => "FocusScript",
            Key.D when ctrl => "FocusConsole",
            Key.D1 when ctrl => "Top",
            Key.D2 when ctrl => "Right",
            Key.D3 when ctrl => "Maximized",
            Key.C when ctrl && shift => "Commands",
            Key.C when ctrl && displayedSession?.Engine.State is SessionState.Running or SessionState.Debugging => "Stop",
            Key.Space when ctrl => "Complete",
            Key.OemPlus or Key.Add when ctrl => "ZoomIn",
            Key.OemMinus or Key.Subtract when ctrl => "ZoomOut",
            _ => null
        };
        if (action is null) return;
        e.Handled = true;
        await GuardAsync(() => ActAsync(action));
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closingApproved) return;
        e.Cancel = true;
        if (closingInProgress) return;
        closingInProgress = true;
        await GuardAsync(async () =>
        {
            foreach (var session in Workbench.Sessions.ToArray())
                if (!await CloseSessionAsync(session)) { closingInProgress = false; return; }
            await settings.SaveAsync();
            closingApproved = true;
            Close();
        });
        closingInProgress = false;
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or RuntimeException or JsonException or NotSupportedException or System.Text.DecoderFallbackException)
        {
            await ReportErrorAsync("Operation failed", exception);
        }
    }

    private async Task ReportErrorAsync(string title, Exception exception)
    {
        System.Diagnostics.Trace.TraceError("{0}: {1}", title, exception);
        StatusText.Text = title + ": " + exception.Message;
        await Dialogs.ChooseAsync(this, title, exception.Message, "OK");
    }
}
