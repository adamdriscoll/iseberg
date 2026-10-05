using System.Management.Automation;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
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
    private readonly string? settingsFilePath;
    private readonly DispatcherTimer outputTimer;
    private readonly DispatcherTimer analysisTimer;
    private readonly DispatcherTimer autoSaveTimer;
    private readonly DispatcherTimer completionTimer;
    private readonly DispatcherTimer persistenceTimer;
    private readonly ScriptRecovery recovery;
    private bool recovering;
    private bool autoSaving;
    private Task autoSaveTask = Task.CompletedTask;
    private Control? editTarget;
    private readonly PowerShellColorizer colorizer = new();
    private readonly BreakpointMargin breakpointMargin;
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
    private CancellationTokenSource? commandFormCancellation;
    private readonly CancellationTokenSource windowCancellation = new();
    private bool windowClosed;
    private bool updatingCommandList;
    private readonly Dictionary<SessionModel, Action<ShowCommandRequest>> showCommandHandlers = [];
    private readonly Dictionary<SessionModel, Action<InputRequest>> inputHandlers = [];
    private readonly Dictionary<SessionModel, Action<CommandErrorRequest>> commandErrorHandlers = [];
    private bool completionPending;
    private IReadOnlySet<CompletionResultType>? automaticCompletionFilter;
    private TextEditor? automaticCompletionEditor;
    private string? completionNotice;
    private GridLength debuggerDockWidth = new(360);
    private GridLength commandDockWidth = new(290);
    private bool sidePaneLayoutInitialized;
    private bool debuggerDockVisible;
    private bool commandDockVisible;
    public WorkbenchModel Workbench { get; } = new();

    public MainWindow() : this([]) { }

    public MainWindow(string[] args, bool initializeOnOpen = true, UserSettings? preferences = null, string? settingsPath = null,
        string? recoveryDirectory = null)
    {
        settingsFilePath = settingsPath;
        workbenchStore = new(settingsPath is null ? null : settingsPath + ".workbench.json");
        recovery = new(recoveryDirectory ?? (settingsPath is null ? null : settingsPath + ".recovery"));
        if (preferences is not null) { settings = preferences.Copy(); settings.Normalize(); }
        startupFiles = args;
        InitializeComponent();
        Icon = AppIcon.Create();
        DataContext = Workbench;
        CommandForm.CommandChanged += RefreshState;
        Workbench.Sessions.CollectionChanged += (_, e) =>
        {
            foreach (var session in e.OldItems?.OfType<SessionModel>() ?? [])
            {
                DetachDebugger(session);
                if (showCommandHandlers.Remove(session, out var handler))
                    session.Engine.ShowCommandRequested -= handler;
                if (inputHandlers.Remove(session, out var inputHandler))
                    session.Engine.InputRequested -= inputHandler;
                if (commandErrorHandlers.Remove(session, out var errorHandler))
                    session.Engine.CommandErrorRequested -= errorHandler;
            }
            foreach (var session in e.NewItems?.OfType<SessionModel>() ?? [])
            {
                AttachDebugger(session);
                Action<ShowCommandRequest> handler = request =>
                    Dispatcher.UIThread.Post(() => ShowConsoleCommand(session, request));
                showCommandHandlers.Add(session, handler);
                session.Engine.ShowCommandRequested += handler;
                Action<InputRequest> inputHandler = request => Dispatcher.UIThread.Post(() => ShowHostInput(session, request));
                inputHandlers.Add(session, inputHandler);
                session.Engine.InputRequested += inputHandler;
                Action<CommandErrorRequest> errorHandler = request => Dispatcher.UIThread.Post(() => ShowCommandError(session, request));
                commandErrorHandlers.Add(session, errorHandler);
                session.Engine.CommandErrorRequested += errorHandler;
            }
        };
        ScriptEditor.Options.IndentationSize = 4;
        ScriptEditor.Options.ConvertTabsToSpaces = true;
        ScriptEditor.Options.HighlightCurrentLine = true;
        ScriptEditor.TextArea.TextView.LineTransformers.Add(colorizer);
        ScriptEditor.TextArea.TextView.BackgroundRenderers.Add(new ScriptAdornments(
            () => displayedFile, () => CurrentFileDebugLocation(), () => settings.Theme));
        breakpointMargin = new(() => displayedFile, () => CurrentFileDebugLocation(), () => settings.Theme)
        {
            Name = "BreakpointGutter",
            Cursor = new Cursor(StandardCursorType.Hand),
            ClipToBounds = true
        };
        AutomationProperties.SetName(breakpointMargin, UiText.Get("BreakpointGutter"));
        ToolTip.SetTip(breakpointMargin, UiText.Get("BreakpointGutterHint"));
        breakpointMargin.BreakpointRequested += async line =>
        {
            if (displayedSession is { } session && displayedFile is { } file)
                await GuardAsync(() => ToggleLineBreakpointAsync(session, file, line));
        };
        ScriptEditor.TextArea.LeftMargins.Insert(0, breakpointMargin);
        ConsoleEditor.TextArea.TextView.LineTransformers.Add(new ConsoleColorizer(() => displayedSession, () => settings.Theme));
        ConsoleEditor.Options.AllowScrollBelowDocument = false;
        ConsoleEditor.Options.CutCopyWholeLine = false;
        ConsoleEditor.Options.ConvertTabsToSpaces = true;
        ScriptEditor.PropertyChanged += (_, e) => { if (e.Property == TextEditor.IsReadOnlyProperty) UpdateMenuState(); };
        ConsoleEditor.PropertyChanged += (_, e) => { if (e.Property == TextEditor.IsReadOnlyProperty) UpdateMenuState(); };
        folding = FoldingManager.Install(ScriptEditor.TextArea);
        search = SearchPanel.Install(ScriptEditor);
        outputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        outputTimer.Tick += (_, _) => FlushOutput();
        analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        analysisTimer.Tick += (_, _) => { analysisTimer.Stop(); AnalyzeScript(); };
        autoSaveTimer = new DispatcherTimer();
        autoSaveTimer.Tick += (_, _) =>
        {
            if (autoSaving || closingInProgress) return;
            autoSaveTask = AutoSaveAsync();
        };
        persistenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        persistenceTimer.Tick += async (_, _) =>
        {
            if (!initialized || closingInProgress || restoringWorkbench) return;
            await GuardAsync(async () =>
            {
                await SaveWorkbenchAsync();
                if (IsActive) await CheckExternalFilesAsync();
            });
        };
        Activated += async (_, _) =>
        {
            if (initialized) await GuardAsync(CheckExternalFilesAsync);
        };
        PropertyChanged += (_, change) =>
        {
            if (initialized && !closingInProgress && (change.Property == BoundsProperty || change.Property == WindowStateProperty))
                CaptureWindowGeometry();
        };
        PositionChanged += (_, _) =>
        {
            if (initialized && !closingInProgress) CaptureWindowGeometry();
        };
        ScriptEditor.TextArea.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !settings.ScriptCompletionOnEnter) completion?.Close();
        }, RoutingStrategies.Tunnel);
        completionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        completionTimer.Tick += async (_, _) =>
        {
            completionTimer.Stop();
            if (automaticCompletionEditor is { IsKeyboardFocusWithin: true } editor && automaticCompletionFilter is { } filter)
                await GuardAsync(() => RequestCompletionAsync(editor, filter));
        };
        ScriptEditor.TextChanged += (_, _) => { analysisTimer.Stop(); analysisTimer.Start(); RefreshCaret(); };
        ScriptEditor.TextArea.Caret.PositionChanged += (_, _) => { completionTimer.Stop(); RefreshCaret(); };
        ScriptEditor.TextArea.TextEntered += (_, e) => ScheduleCompletion(ScriptEditor, e.Text);
        ConsoleEditor.TextArea.TextEntered += (_, e) => ScheduleCompletion(ConsoleEditor, e.Text);
        ConsoleEditor.TextArea.Caret.PositionChanged += (_, _) => { completionTimer.Stop(); RefreshCaret(); };
        ConsoleEditor.TextArea.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !settings.ConsoleCompletionOnEnter) completion?.Close();
        }, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is not Control source) return;
            var input = source.GetVisualAncestors().Prepend(source).FirstOrDefault(v => v is TextBox or TextEditor);
            if (input is Control control) { editTarget = control; UpdateMenuState(); }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        ConsoleEditor.ContextMenu = CreateEditorMenu(ConsoleEditor);
        ScriptEditor.ContextMenu = CreateEditorMenu(ScriptEditor);
        ConfigureMenus();
        DesktopTheme.Changed += ApplyAppearance;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        ConsoleEditor.TextArea.AddHandler(KeyDownEvent, OnConsoleKeyDown, RoutingStrategies.Tunnel);
        if (initializeOnOpen && !Design.IsDesignMode)
        {
            Opened += async (_, _) => await GuardAsync(StartAsync);
            Closing += OnClosing;
        }
        Closed += (_, _) =>
        {
            windowClosed = true;
            ++commandRequestVersion;
            windowCancellation.Cancel();
            outputTimer.Stop(); analysisTimer.Stop(); autoSaveTimer.Stop(); completionTimer.Stop(); persistenceTimer.Stop();
            completion?.Close();
            commandFormCancellation?.Cancel();
            commandFormCancellation?.Dispose();
            commandFormCancellation = null;
            windowCancellation.Dispose();
            foreach (var (session, handler) in showCommandHandlers)
                session.Engine.ShowCommandRequested -= handler;
            showCommandHandlers.Clear();
            foreach (var (session, handler) in inputHandlers)
                session.Engine.InputRequested -= handler;
            inputHandlers.Clear();
            foreach (var (session, handler) in commandErrorHandlers)
                session.Engine.CommandErrorRequested -= handler;
            commandErrorHandlers.Clear();
            foreach (var session in debuggerHandlers.Keys.ToArray()) DetachDebugger(session);
            DesktopTheme.Changed -= ApplyAppearance;
            foreach (var path in printPreviews)
            {
                try { File.Delete(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceError("Could not remove print preview: {0}", exception); }
            }
        };
        debuggerDockWidth = new(settings.Geometry.DebuggerWidth);
        commandDockWidth = new(settings.Geometry.CommandsWidth);
        ApplySettings();
        if (preferences is not null) ApplyWindowGeometry();
    }

    private async Task StartAsync()
    {
        try { settings = await UserSettings.LoadAsync(settingsFilePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            await ReportErrorAsync("Could not load settings", exception);
        }
        ApplyWindowGeometry();
        debuggerDockWidth = new(settings.Geometry.DebuggerWidth);
        commandDockWidth = new(settings.Geometry.CommandsWidth);
        sidePaneLayoutInitialized = false;
        appliedPaneLayout = null;
        initialized = true;
        ApplySettings();
        PopulateRecentMenu();
        try { await RestoreWorkbenchAsync(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { await ReportErrorAsync(UiText.Get("RestoreWorkbenchFailed"), exception); }
        if (Workbench.Sessions.Count == 0) await NewSessionAsync();
        await RecoverScriptsAsync();
        foreach (var path in startupFiles)
            if (!path.StartsWith("--", StringComparison.Ordinal))
                await OpenFileAsync(path);
        outputTimer.Start();
        persistenceTimer.Start();
        await SaveWorkbenchAsync();
        ScriptEditor.TextArea.Focus();
    }

    private DebugLocation? CurrentFileDebugLocation() =>
        displayedSession is { } session && displayedFile is { } file && FileInCurrentRunspace(session, file)
            ? session.DebugLocation : null;

    private async Task NewSessionAsync(System.Management.Automation.Runspaces.RunspaceConnectionInfo? connection = null,
        string? name = null, bool createDocument = true)
    {
        var session = new SessionModel(name ?? $"PowerShell {++sessionNumber}");
        if (name is not null) sessionNumber = Math.Max(sessionNumber, int.Parse(name["PowerShell ".Length..]));
        session.Engine.ConsoleCleared += () => Dispatcher.UIThread.Post(() => session.ClearOutput());
        session.Engine.ProgressChanged += progress => Dispatcher.UIThread.Post(() =>
        {
            if (session != displayedSession) return;
            ProgressPanel.IsVisible = !progress.Completed;
            ProgressText.Text = progress.Activity + " - " + progress.Status;
            ScriptProgress.IsIndeterminate = progress.Percent < 0;
            ScriptProgress.Value = Math.Max(0, progress.Percent);
        });
        Workbench.Sessions.Add(session);
        Workbench.SelectedSession = session;
        if (createDocument) NewFile();
        DisplaySession();
        try
        {
            await session.Engine.InitializeAsync();
            session.Console.HidePrompt();
            session.Console.Append(new($"PowerShell {session.Engine.Version}\nCopyright (c) Microsoft Corporation.\n\n", OutputKind.Output));
            if (settings.LoadProfiles) await LoadProfilesAsync(session);
            await RestoreDebuggerSettingsAsync(session);
            if (connection is not null) await session.Engine.ConnectAsync(connection);
            if (connection is null) await RefreshCommandsAsync(session);
        }
        catch (Exception exception) when (exception is RuntimeException or InvalidOperationException or IOException)
        {
            session.Console.Append(new($"Session initialization failed: {exception.Message}\n", OutputKind.Error));
            await ReportErrorAsync("Could not initialize PowerShell", exception);
        }
        RefreshState();
        FocusConsoleInput();
    }

    private void NewFile()
    {
        if (Workbench.SelectedSession is not { } session) return;
        string name;
        do { name = $"Untitled{++fileNumber}.ps1"; }
        while (Workbench.Sessions.SelectMany(candidate => candidate.Files).Any(tab => tab.File.Name == name));
        var file = new ScriptTab(new ScriptFile(name));
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
        completion?.Close();
        completionTimer.Stop();
        if (displayedSession is not null)
            displayedSession.ConsoleCaretOffset = ConsoleEditor.CaretOffset;
        displayedSession = next;
        WatchExpression.Text = "";
        RenderDebugger();
        completionNotice = null;
        SetCommandModules();
        if (next is null) return;
        ConsoleEditor.Document = next.ConsoleDocument;
        ConsoleEditor.TextArea.ReadOnlySectionProvider = next.Console;
        ConsoleEditor.CaretOffset = Math.Min(next.ConsoleCaretOffset, next.ConsoleDocument.TextLength);
        ProgressPanel.IsVisible = false;
        DisplayFile();
        RefreshState();
        ConsoleEditor.ScrollToEnd();
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
                completion?.Close();
                ConsoleEditor.TextArea.TextView.Redraw();
                ConsoleEditor.ScrollToEnd();
            }
        }
        RefreshState();
    }

    private void AnalyzeScript()
    {
        colorizer.Analysis = EditorAnalysis.Analyze(ScriptEditor.Text);
        ScriptEditor.TextArea.TextView.Redraw();
        breakpointMargin.InvalidateVisual();
        folding.UpdateFoldings(settings.ShowOutlining ? colorizer.Analysis.Folds.Select(f => new NewFolding(f.Start, f.End)) : [], -1);
        Diagnostics.IsVisible = colorizer.Analysis.Errors.Length > 0;
        Diagnostics.Text = string.Join("  |  ", colorizer.Analysis.Errors.Take(3).Select(
            error => $"Line {error.Extent.StartLineNumber}: {error.Message}"));
    }

    private void RefreshCaret()
    {
        var editor = editTarget as TextEditor ?? ScriptEditor;
        CaretText.Text = string.Format(UiText.Get("Caret"), editor.TextArea.Caret.Line, editor.TextArea.Caret.Column);
        EncodingText.Text = displayedFile?.File.EncodingName ?? "";
        Title = $"{displayedFile?.File.Title ?? "Iseberg"} - Iseberg - PowerShell 7 ISE";
    }

    private void RefreshState()
    {
        var state = displayedSession?.Engine.State ?? SessionState.Starting;
        var ready = state == SessionState.Ready;
        var paused = displayedSession?.Engine.IsDebuggerPaused == true;
        var nested = displayedSession?.Engine.IsNestedPromptActive == true;
        RunButton.IsEnabled = displayedFile is not null && (ready || paused) && displayedSession?.Evaluating != true;
        SelectionButton.IsEnabled = displayedFile is not null && (ready || paused || nested) && displayedSession?.Evaluating != true;
        StopButton.IsEnabled = state is SessionState.Running or SessionState.Debugging or SessionState.NestedPrompt;
        var validCommand = CommandForm.Result is { IsValid: true };
        CommandRunButton.IsEnabled = ready && validCommand;
        CommandCopyButton.IsEnabled = validCommand;
        CommandInsertButton.IsEnabled = validCommand && displayedFile is not null && !paused;
        CommandHelpButton.IsEnabled = ready && CommandList.SelectedItem is not null;
        CommandRefreshButton.IsEnabled = ready;
        CommandList.IsEnabled = ready;
        var canEvaluate = (paused || nested) && displayedSession?.Evaluating != true;
        CallStackList.IsEnabled = paused && canEvaluate;
        ConsoleEditor.IsReadOnly = closingInProgress || !(ready || canEvaluate);
        ScriptEditor.IsReadOnly = closingInProgress || paused;
        if (!ready && !canEvaluate)
        {
            completionNotice = null;
            if (completion?.TextArea == ConsoleEditor.TextArea) completion.Close();
            displayedSession?.Console.HidePrompt();
        }
        else displayedSession?.FlushOutput();
        StatusText.Text = state switch
        {
            SessionState.Ready => completionNotice ?? UiText.Get("Ready"),
            SessionState.Running => UiText.Get(displayedSession?.EditingBreakpoints == true ? "PauseForBreakpointEdit" : "Running"),
            SessionState.Debugging => string.Format(UiText.Get("DebugStatus"), displayedSession?.DebugLocation?.Line),
            SessionState.NestedPrompt => UiText.Get("NestedPromptStatus"),
            SessionState.Disposed => UiText.Get("SessionClosed"),
            _ => UiText.Get("Starting")
        };
        UpdateMenuState();
        UpdateBreakpointControls();
        RefreshCaret();
    }

    private async void OnAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string action })
            await GuardAsync(() => ActAsync(action));
    }

    private async Task ActAsync(string action)
    {
        if (closingInProgress) return;
        var session = Workbench.SelectedSession;
        switch (action)
        {
            case "New": NewFile(); break;
            case "Open": await PickFilesAsync(); break;
            case "Save": if (displayedFile is not null) await SaveFileAsync(displayedFile); break;
            case "SaveAs": if (displayedFile is not null) await SaveFileAsync(displayedFile, true); break;
            case "Encoding": await ChooseEncodingAsync(); break;
            case "Reload": if (displayedFile is not null) await ReloadFileAsync(displayedFile); break;
            case "SaveAll":
                foreach (var tab in Workbench.Sessions.SelectMany(s => s.Files).ToArray())
                    if (tab.File.IsDirty && !await SaveFileAsync(tab)) break;
                break;
            case "Close": if (session is not null && displayedFile is not null) await CloseFileAsync(session, displayedFile); break;
            case "NewSession": await NewSessionAsync(); break;
            case "NewRemoteSession": await NewRemoteSessionAsync(); break;
            case "OpenRemoteFile": await PickRemoteFileAsync(); break;
            case "ExitRemoteSession": if (session is not null) await session.Engine.ExitRemoteSessionAsync(); break;
            case "CloseSession": if (session is not null) await CloseSessionAsync(session); break;
            case "Exit": Close(); break;
            case "Undo": if (editTarget is TextBox undoBox) undoBox.Undo(); else (editTarget as TextEditor ?? ScriptEditor).Undo(); break;
            case "Redo": if (editTarget is TextBox redoBox) redoBox.Redo(); else (editTarget as TextEditor ?? ScriptEditor).Redo(); break;
            case "Cut": if (editTarget is TextBox cutBox) cutBox.Cut(); else (editTarget as TextEditor ?? ScriptEditor).Cut(); break;
            case "Copy": if (editTarget is TextBox copyBox) copyBox.Copy(); else (editTarget as TextEditor ?? ScriptEditor).Copy(); break;
            case "Paste": if (editTarget is TextBox pasteBox) pasteBox.Paste(); else (editTarget as TextEditor ?? ScriptEditor).Paste(); break;
            case "SelectAll": if (editTarget is TextBox selectBox) selectBox.SelectAll(); else (editTarget as TextEditor ?? ScriptEditor).SelectAll(); break;
            case "Find": search.Open(); search.Reactivate(); break;
            case "Replace": await ReplaceAsync(); break;
            case "GoToLine": await GoToLineAsync(); break;
            case "MatchBrace": case "SelectBrace": NavigateBrace(action == "SelectBrace"); break;
            case "Run": if (session?.Engine.State == SessionState.Debugging) session.Engine.Resume(DebuggerResumeAction.Continue); else await RunScriptAsync(false); break;
            case "RunSelection": await RunScriptAsync(true); break;
            case "Stop": if (session is not null) await session.Engine.StopAsync(); break;
            case "Continue": session?.Engine.Resume(DebuggerResumeAction.Continue); break;
            case "BreakAll": session?.Engine.BreakAll(); break;
            case "StepInto": session?.Engine.Resume(DebuggerResumeAction.StepInto); break;
            case "StepOver": session?.Engine.Resume(DebuggerResumeAction.StepOver); break;
            case "StepOut": session?.Engine.Resume(DebuggerResumeAction.StepOut); break;
            case "Breakpoint":
                if (session is not null && displayedFile is not null)
                    await ToggleLineBreakpointAsync(session, displayedFile);
                break;
            case "RemoveBreakpoints":
                if (session is not null)
                {
                    await PrepareBreakpointEditAsync(session);
                    await session.Engine.RemoveAllBreakpointsAsync();
                    foreach (var file in session.Files)
                        file.ClearBreakpoints();
                    await RefreshDebuggerAsync(session);
                }
                ScriptEditor.TextArea.TextView.Redraw();
                break;
            case "DebuggerPanes":
                if (session is not null)
                {
                    session.DebuggerPaneVisible = !session.DebuggerPaneVisible;
                    if (session.DebuggerPaneVisible)
                        await RefreshDebuggerAsync(session);
                    RenderDebugger();
                }
                break;
            case "RefreshDebugger": if (session is not null) await RefreshDebuggerAsync(session); break;
            case "NewBreakpoint": if (session is not null) await EditBreakpointAsync(session, true); break;
            case "EditBreakpoint": if (session is not null) await EditBreakpointAsync(session, false); break;
            case "EnableBreakpoint":
                if (session is not null && BreakpointsList.SelectedItem is DebugBreakpoint breakpoint)
                {
                    await PrepareBreakpointEditAsync(session);
                    await session.Engine.SetBreakpointEnabledAsync(breakpoint.Id, !breakpoint.Spec.Enabled);
                    ApplyBreakpointChange(session, breakpoint.Spec, breakpoint.Spec with { Enabled = !breakpoint.Spec.Enabled });
                    await RefreshDebuggerAsync(session, reconcile: true);
                }
                break;
            case "DeleteBreakpoint":
                if (session is not null && BreakpointsList.SelectedItem is DebugBreakpoint deleted)
                {
                    await PrepareBreakpointEditAsync(session);
                    await session.Engine.RemoveBreakpointAsync(deleted.Id);
                    ApplyBreakpointChange(session, deleted.Spec, null);
                    await RefreshDebuggerAsync(session, reconcile: true);
                }
                break;
            case "AddWatch":
                if (session is not null && !string.IsNullOrWhiteSpace(WatchExpression.Text))
                {
                    session.Watches.Add(WatchExpression.Text);
                    WatchExpression.Text = "";
                    await RefreshDebuggerAsync(session);
                    await SaveDebuggerSettingsAsync(session);
                    RenderDebugger();
                }
                break;
            case "RemoveWatch":
                if (session is not null && WatchesList.SelectedItem is TreeViewItem watch &&
                    WatchesList.Items.Contains(watch))
                {
                    session.Watches.RemoveAt(WatchesList.Items.IndexOf(watch));
                    await RefreshDebuggerAsync(session);
                    await SaveDebuggerSettingsAsync(session);
                    RenderDebugger();
                }
                break;
            case "Complete": await ShowCompletionAsync(); break;
            case "ShowCommand": await ShowCommandAsync(); break;
            case "Snippets": await InsertSnippetAsync(); break;
            case "CreateSnippet": await CreateSnippetAsync(); break;
            case "ImportSnippets": await ImportSnippetsAsync(); break;
            case "ExportSnippets": await ExportSnippetsAsync(); break;
            case "Clear": session?.ClearOutput(); break;
            case "Top": case "Right": case "Maximized": settings.Layout = action; ApplySettings(); break;
            case "Commands": settings.ShowCommands = !settings.ShowCommands; ApplySettings(); break;
            case "LineNumbers": settings.ShowLineNumbers = !settings.ShowLineNumbers; ApplySettings(); break;
            case "WordWrap": settings.WordWrap = !settings.WordWrap; ApplySettings(); break;
            case "ZoomIn": ZoomSlider.Value = Math.Min(400, ZoomSlider.Value + 5); break;
            case "ZoomOut": ZoomSlider.Value = Math.Max(20, ZoomSlider.Value - 5); break;
            case "Fold" when settings.ShowOutlining:
                var collapse = folding.AllFoldings.Any(f => !f.IsFolded);
                foreach (var fold in folding.AllFoldings) fold.IsFolded = collapse;
                break;
            case "FocusScript": ScriptEditor.TextArea.Focus(); break;
            case "FocusConsole": if (settings.Layout == "Maximized") { settings.Layout = "Top"; ApplySettings(); } FocusConsoleInput(); break;
            case "RefreshCommands": if (session is not null) await RefreshCommandsAsync(session); break;
            case "InsertCommand": InsertCommand(); break;
            case "CopyCommand":
                var clipboard = GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException(UiText.Get("ClipboardUnavailable"));
                await clipboard.SetTextAsync(CommandForm.GetCommand());
                break;
            case "RunCommand":
                if (session is not null)
                {
                    var script = CommandForm.GetCommand();
                    await session.Engine.ExecuteAsync(script);
                    FlushOutput();
                    FocusConsoleInput();
                }
                break;
            case "CommandHelp": if (CommandList.SelectedItem is CommandDescription selected) await ShowHelpAsync(selected.Name); break;
            case "Help": await ShowHelpAsync(ScriptEditor.SelectedText.Length > 0 ? ScriptEditor.SelectedText : CommandAtCaret()); break;
            case "Profiles": if (session is not null) await LoadProfilesAsync(session); break;
            case "AutoProfiles": settings.LoadProfiles = !settings.LoadProfiles; UpdateMenuState(); await settings.SaveAsync(settingsFilePath); break;
            case "ExecutionPolicy":
                if (session is not null && OperatingSystem.IsWindows() &&
                    await Dialogs.ChooseAsync(this, "Execution policy",
                        "Set RemoteSigned for this application process? Local scripts can run; downloaded scripts still require a trusted signature. Machine and user settings are not changed, and Group Policy still applies.",
                        "Enable", "Cancel") == "Enable")
                    await session.Engine.ExecuteAsync("Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force");
                break;
            case "Options": await OptionsAsync(); break;
            case "Print": await PrintScriptAsync(); break;
            case "About":
                await Dialogs.ShowTextAsync(this, "About Iseberg",
                    $"Iseberg\nA cross-platform PowerShell ISE-style editor and terminal.\n\nPowerShell {session?.Engine.Version}\nAvalonia + AvaloniaEdit + PowerShell SDK\n\nSee README.md for implemented behavior and GitHub issues for remaining work.");
                break;
        }
        if (action is "Copy" or "Cut" or "Paste" or "Undo" or "Redo" or "SelectAll") FocusInput(editTarget);
        if (initialized && !closingInProgress && !windowClosed) await SaveWorkbenchAsync();
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
        if (windowClosed || closingInProgress) throw new InvalidOperationException("The workbench is closing.");
        if (Workbench.SelectedSession is not { } session) return;
        var fullPath = Path.GetFullPath(path);
        if (settings.WarnDuplicateFiles && Workbench.Sessions.Where(s => s != session).SelectMany(s => s.Files)
                .Any(t => !t.File.IsRemote && string.Equals(t.File.Path, fullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) &&
            await Dialogs.ChooseAsync(this, "Duplicate file", "This script is already open in another PowerShell tab. Open another editable copy?", "Open", "Cancel") != "Open")
            return;
        var existing = session.Files.FirstOrDefault(tab => !tab.File.IsRemote && string.Equals(tab.File.Path, fullPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var file = existing is null ? await ReadFileAsync(session, fullPath, remote: false) : existing.File;
        if (file is null) return;
        if (windowClosed || closingInProgress) throw new InvalidOperationException("The workbench closed while reading the file.");
        var tab = existing ?? new ScriptTab(file);
        if (existing is null)
        {
            tab.ReplaceBreakpoints(session.Breakpoints.Where(breakpoint => breakpoint.Spec.Kind == BreakpointKind.Line &&
                !session.Engine.IsRemote && SameScript(breakpoint.Spec.ScriptPath, fullPath)).Select(breakpoint => breakpoint.Spec));
            session.Files.Add(tab);
        }
        session.SelectedFile = tab;
        DisplayFile();
        RememberFile(fullPath);
        if (initialized && !restoringWorkbench) await SaveWorkbenchAsync();
    }

    private async Task<bool> SaveFileAsync(ScriptTab tab, bool saveAs = false)
    {
        var owner = Workbench.Sessions.First(s => s.Files.Contains(tab));
        if (tab.File.IsRemote || tab.File.Path is null && owner.Engine.IsRemote)
        {
            var previousRemotePath = tab.File.Path;
            var remotePath = tab.File.Path;
            if (remotePath is null || saveAs)
                remotePath = await Dialogs.AskAsync(this, UiText.Get("SaveRemoteFile"), UiText.Get("RemoteFilePath"), remotePath ?? "");
            if (remotePath is null) return false;
            if (!await SaveWithConflictCheckAsync(owner, tab, remotePath, remote: true)) return false;
            await autoSaveTask;
            recovery.Remove(tab.RecoveryId);
            if (previousRemotePath is not null && !string.Equals(previousRemotePath, tab.File.Path, StringComparison.Ordinal))
                await owner.Engine.SetBreakpointsAsync(previousRemotePath, []);
            await owner.Engine.SetLineBreakpointsAsync(tab.File.Path!, tab.LineBreakpoints);
            tab.AcknowledgeBreakpointLines();
            await RefreshDebuggerAsync(owner, reconcile: true);
            RefreshCaret();
            return true;
        }
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
        if (!await SaveWithConflictCheckAsync(owner, tab, path, remote: false)) return false;
        await autoSaveTask;
        recovery.Remove(tab.RecoveryId);
        if (FileInCurrentRunspace(owner, tab) && (owner.Engine.State == SessionState.Ready || owner.Engine.IsDebuggerPaused))
        {
            if (previousPath is not null && !SameScript(previousPath, tab.File.Path))
                await owner.Engine.SetBreakpointsAsync(previousPath, []);
            await owner.Engine.SetLineBreakpointsAsync(tab.File.Path!, tab.LineBreakpoints);
            tab.AcknowledgeBreakpointLines();
            await RefreshDebuggerAsync(owner, reconcile: true);
        }
        RememberFile(path);
        RefreshCaret();
        return true;
    }

    private void RememberFile(string path)
    {
        settings.RecentFiles.RemoveAll(p => string.Equals(p, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        settings.RecentFiles.Insert(0, path);
        if (settings.RecentFiles.Count > settings.RecentFileCount) settings.RecentFiles.RemoveRange(settings.RecentFileCount, settings.RecentFiles.Count - settings.RecentFileCount);
        PopulateRecentMenu();
    }

    private void PopulateRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var recent in settings.RecentFiles.Take(settings.RecentFileCount))
        {
            var item = new MenuItem { Header = $"_{RecentMenu.Items.Count + 1} {recent.Replace("_", "__")}" };
            item.Click += async (_, _) => await GuardAsync(() => OpenFileAsync(recent));
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = RecentMenu.Items.Count > 0;
    }

    private async Task<bool> ConfirmSaveAsync(ScriptTab tab)
    {
        if (!tab.File.IsDirty) return true;
        var result = await Dialogs.ChooseAsync(this, "Save changes", $"Save changes to {tab.File.Name}?", "Save", "Don't Save", "Cancel");
        if (result == "Don't Save") return true;
        if (result != "Save" || !await SaveFileAsync(tab)) return false;
        if (tab.File.IsDirty) throw new InvalidOperationException("The document was edited while saving. Closing was canceled to preserve those edits.");
        return true;
    }

    private async Task CloseFileAsync(SessionModel session, ScriptTab tab)
    {
        if (session.Engine.State is SessionState.Running or SessionState.Debugging or SessionState.NestedPrompt)
            throw new InvalidOperationException("Stop execution before closing a script.");
        if (!await ConfirmSaveAsync(tab)) return;
        await autoSaveTask;
        recovery.Remove(tab.RecoveryId);
        if (tab.File.Path is not null && FileInCurrentRunspace(session, tab)) await session.Engine.SetBreakpointsAsync(tab.File.Path, []);
        session.Files.Remove(tab);
        await RefreshDebuggerAsync(session, reconcile: true);
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
        if (session.Engine.State is SessionState.Running or SessionState.Debugging or SessionState.NestedPrompt)
        {
            if (await Dialogs.ChooseAsync(this, "Stop execution", "Stop the running command and close this PowerShell tab?", "Stop", "Cancel") != "Stop")
                return false;
            await session.Engine.StopAsync();
        }
        foreach (var file in session.Files)
            if (!await ConfirmSaveAsync(file)) return false;
        if (session.Engine.State == SessionState.Ready)
            await RefreshDebuggerAsync(session, reconcile: true);
        await SaveDebuggerSettingsAsync(session);
        await autoSaveTask;
        foreach (var file in session.Files) recovery.Remove(file.RecoveryId);
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
            if (session.Engine.State == SessionState.Debugging) await EvaluateConsoleAsync(session, text);
            else if (session.Engine.IsNestedPromptActive) await EvaluateNestedConsoleAsync(session, text);
            else await session.Engine.ExecuteAsync(text);
        }
        else
        {
            if (file.File.IsRemote && !FileInCurrentRunspace(session, file))
                throw new InvalidOperationException("Reopen this script in its remote connection before running it.");
            if (session.Engine.IsRemote && file.File.Path is not null && !file.File.IsRemote)
                throw new InvalidOperationException("Open or save the script on the remote machine before running a named script. F8 can run local text remotely.");
            if (settings.PromptToSaveBeforeRun && file.File.IsDirty)
            {
                var answer = await Dialogs.ChooseAsync(this, "Save before running", $"Save {file.File.Name} before running?", "Save", "Run without saving", "Cancel");
                if (answer is null or "Cancel") return;
                if (answer == "Save" && !await SaveFileAsync(file)) return;
                if (answer == "Run without saving")
                {
                    if (file.File.Breakpoints.Count > 0) throw new InvalidOperationException("Save this script to run with breakpoints.");
                    await session.Engine.ExecuteAsync(file.File.Text);
                    await RefreshDebuggerAsync(session, reconcile: true);
                    FlushOutput(); RefreshState(); return;
                }
            }
            if (file.File.Path is not null || file.File.Breakpoints.Count > 0)
            {
                if ((file.File.IsDirty || file.File.Path is null) && !await SaveFileAsync(file)) return;
                foreach (var tab in session.Files.Where(tab => tab.File.Path is not null && FileInCurrentRunspace(session, tab)))
                {
                    await session.Engine.SetLineBreakpointsAsync(tab.File.Path!, tab.LineBreakpoints);
                    tab.AcknowledgeBreakpointLines();
                }
            }
            await session.Engine.ExecuteAsync(file.File.Text, file.File.Path);
        }
        await RefreshDebuggerAsync(session, reconcile: true);
        FlushOutput();
        RefreshState();
    }

    private async void OnConsoleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (displayedSession is not { } session || session.Evaluating ||
            session.Engine.State is not (SessionState.Ready or SessionState.Debugging or SessionState.NestedPrompt)) return;
        if (completion is not null && (e.Key == Key.Tab || e.Key == Key.Enter && settings.ConsoleCompletionOnEnter)) return;
        if (e.Key == Key.Escape)
        {
            if (completion is not null) { completion.Close(); e.Handled = true; return; }
            session.Input = "";
            session.Completion = null;
            FocusConsoleInput();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (ConsoleEditor.CaretOffset < session.Console.InputStart)
            {
                var command = session.OutputSpans.FirstOrDefault(s => s.Kind == OutputKind.Command &&
                    s.Start <= ConsoleEditor.CaretOffset && s.End > ConsoleEditor.CaretOffset && s.CodeStart >= 0);
                if (command is not null)
                    session.Input = ConsoleEditor.Document.GetText(command.CodeStart, command.End - command.CodeStart).TrimEnd('\r', '\n');
                FocusConsoleInput();
                return;
            }
            var text = session.Input;
            if (EditorAnalysis.Analyze(text).Errors.Any(error => error.IncompleteInput))
            {
                ConsoleEditor.TextArea.PerformTextInput(Environment.NewLine);
                return;
            }
            if (string.IsNullOrWhiteSpace(text)) return;
            session.Input = "";
            session.Console.HidePrompt();
            completion?.Close();
            completionTimer.Stop();
            if (session.History.Count == 0 || session.History[^1] != text) session.History.Add(text);
            if (session.History.Count > 1000) session.History.RemoveAt(0);
            session.HistoryIndex = session.History.Count;
            session.DraftInput = "";
            session.Completion = null;
            await GuardAsync(async () =>
            {
                if (session.Engine.State == SessionState.Debugging) await EvaluateConsoleAsync(session, text);
                else if (session.Engine.IsNestedPromptActive) await EvaluateNestedConsoleAsync(session, text);
                else
                {
                    await session.Engine.ExecuteAsync(text);
                    await RefreshDebuggerAsync(session, reconcile: true);
                }
                FlushOutput();
                FocusConsoleInput();
            });
        }
        else if (e.Key is Key.Up or Key.Down && e.KeyModifiers == KeyModifiers.None && !session.Input.Contains('\n') &&
                 ConsoleEditor.CaretOffset >= session.Console.InputStart && completion is null)
        {
            e.Handled = true;
            if (session.HistoryIndex == session.History.Count) session.DraftInput = session.Input;
            session.HistoryIndex = Math.Clamp(session.HistoryIndex + (e.Key == Key.Up ? -1 : 1), 0, session.History.Count);
            session.Input = session.HistoryIndex == session.History.Count ? session.DraftInput : session.History[session.HistoryIndex];
            FocusConsoleInput();
        }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            if ((session.Engine.State == SessionState.Ready || session.Engine.IsDebuggerPaused || session.Engine.IsNestedPromptActive) && ConsoleEditor.CaretOffset >= session.Console.InputStart)
                await GuardAsync(() => CompleteConsoleAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
        }
    }

    private void FocusConsoleInput()
    {
        ConsoleEditor.TextArea.Focus();
        ConsoleEditor.CaretOffset = ConsoleEditor.Document.TextLength;
        ConsoleEditor.TextArea.ClearSelection();
        ConsoleEditor.ScrollToEnd();
    }

    private async Task CompleteConsoleAsync(bool backwards = false)
    {
        if (displayedSession is not { } session) return;
        if (completionPending) return;
        var text = session.Input;
        var revision = session.DebugRevisionCounter;
        var caret = ConsoleEditor.CaretOffset - session.Console.InputStart;
        var previous = session.Completion;
        var cycling = previous is not null && previous.LastText == text && previous.LastCaret == caret;
        var matches = cycling ? previous!.Results : await GetCompletionAsync(session, text, caret);
        if (matches is null || session != displayedSession || revision != session.DebugRevisionCounter || text != session.Input ||
            caret != ConsoleEditor.CaretOffset - session.Console.InputStart || matches.Matches.Count == 0) return;
        var index = cycling ? (previous!.Index + (backwards ? -1 : 1) + matches.Matches.Count) % matches.Matches.Count : (backwards ? matches.Matches.Count - 1 : 0);
        var original = cycling ? previous!.Original : text;
        var match = matches.Matches[index];
        session.Input = original.Remove(matches.Start, matches.Length).Insert(matches.Start, match.CompletionText);
        ConsoleEditor.CaretOffset = session.Console.InputStart + matches.Start + match.CompletionText.Length;
        session.Completion = new(original, session.Input, ConsoleEditor.CaretOffset - session.Console.InputStart, index, matches);
    }

    private void ApplyAppearance()
    {
        var family = new FontFamily(settings.FontFamily + ", Consolas, DejaVu Sans Mono, Menlo, monospace");
        ScriptEditor.FontFamily = ConsoleEditor.FontFamily = family;
        IBrush ColorBrush(string key, string system) => DesktopTheme.HighContrast ? DesktopTheme.Brush(system) :
            new SolidColorBrush(Color.Parse(settings.Theme.Colors[key]));
        ScriptEditor.Background = ScriptPane.Background = ColorBrush("Script.Background", "WindowBrush");
        ScriptEditor.Foreground = ColorBrush("Script.Foreground", "WindowTextBrush");
        ConsolePane.Background = ColorBrush("Console.Background", "WindowBrush");
        ConsoleEditor.Background = ColorBrush("Console.TextBackground", "WindowBrush");
        ConsoleEditor.Foreground = ColorBrush("Console.Foreground", "WindowTextBrush");
        colorizer.Theme = settings.Theme;
        ScriptEditor.Options.HighlightCurrentLine = !DesktopTheme.HighContrast;
        ScriptEditor.LineNumbersForeground = DesktopTheme.Brush("DisabledTextBrush");
        ScriptEditor.TextArea.SelectionBrush = ConsoleEditor.TextArea.SelectionBrush = DesktopTheme.Brush("SelectionBrush");
        ScriptEditor.TextArea.SelectionForeground = ConsoleEditor.TextArea.SelectionForeground = DesktopTheme.Brush("SelectionTextBrush");
        ScriptEditor.TextArea.Caret.CaretBrush = ScriptEditor.Foreground;
        ConsoleEditor.TextArea.Caret.CaretBrush = ConsoleEditor.Foreground;
        ScriptEditor.TextArea.TextView.Redraw();
        ConsoleEditor.TextArea.TextView.Redraw();
    }

    private ContextMenu CreateEditorMenu(Control editor)
    {
        var menu = new ContextMenu();
        foreach (var action in new[] { "Undo", "Redo", "Cut", "Copy", "Paste", "SelectAll" })
        {
            var item = new MenuItem { Header = UiText.Get(action), Tag = action, Icon = new ToolbarIcon { Kind = action } };
            item.Click += OnAction;
            menu.Items.Add(item);
        }
        menu.Opening += (_, _) =>
        {
            editTarget = editor;
            foreach (var item in menu.Items.OfType<MenuItem>())
                item.IsEnabled = item.Tag is string action && CanEdit(action);
        };
        return menu;
    }

    private IEnumerable<MenuItem> ActionMenus()
    {
        foreach (var top in WorkbenchMenu.Items.OfType<MenuItem>())
            foreach (var item in top.Items.OfType<MenuItem>())
                yield return item;
    }

    private void ConfigureMenus()
    {
        var names = new[] { "FileMenu", "EditMenu", "ViewMenu", "ToolsMenu", "DebugMenu", "AddonsMenu", "HelpMenu" };
        foreach (var (menu, index) in WorkbenchMenu.Items.OfType<MenuItem>().Select((menu, index) => (menu, index)))
        {
            menu.Header = UiText.Get(names[index]);
            menu.SubmenuOpened += (_, _) => UpdateMenuState();
        }
        foreach (var item in ActionMenus())
        {
            if (item == RecentMenu) { item.Header = UiText.Get("RecentFiles"); continue; }
            if (item.Tag is not string action) continue;
            var key = action switch { "Top" => "PaneTop", "Right" => "PaneRight", "Maximized" => "PaneMaximized", "Options" => "OptionsMenu", _ => action };
            item.Header = UiText.Get(key);
            item.Icon = new ToolbarIcon { Kind = action };
            if (action is "Top" or "Right" or "Maximized" or "Commands" or "LineNumbers" or "WordWrap" or "DebuggerPanes")
                item.ToggleType = MenuItemToggleType.CheckBox;
            AutomationProperties.SetName(item, UiText.Get(key).Replace("_", ""));
        }
        foreach (var button in WorkbenchToolbar.GetLogicalDescendants().OfType<Button>())
        {
            button.Focusable = false;
            if (button.Tag is not string action) continue;
            var key = action switch { "Top" => "PaneTop", "Right" => "PaneRight", "Maximized" => "PaneMaximized", _ => action };
            var label = UiText.Get(key).Replace("_", "").TrimEnd('.');
            AutomationProperties.SetName(button, label);
            var gesture = ActionMenus().FirstOrDefault(m => m.Tag as string == action)?.InputGesture;
            ToolTip.SetTip(button, gesture is null ? label : $"{label} ({gesture})");
        }
    }

    private void UpdateMenuState()
    {
        if (WorkbenchMenu is null) return;
        var state = displayedSession?.Engine.State;
        var ready = state == SessionState.Ready;
        var paused = displayedSession?.Engine.IsDebuggerPaused == true;
        var nested = displayedSession?.Engine.IsNestedPromptActive == true;
        foreach (var item in ActionMenus())
        {
            if (item.Tag is not string action) continue;
            item.IsChecked = action switch
            {
                "Top" or "Right" or "Maximized" => action == settings.Layout,
                "Commands" => settings.ShowCommands,
                "LineNumbers" => settings.ShowLineNumbers,
                "WordWrap" => settings.WordWrap,
                "AutoProfiles" => settings.LoadProfiles,
                "DebuggerPanes" => displayedSession?.DebuggerPaneVisible == true,
                _ => false
            };
            item.IsEnabled = action switch
            {
                "Run" => displayedFile is not null && (ready || paused) && displayedSession?.Evaluating != true,
                "RunSelection" => displayedFile is not null && (ready || paused || nested) && displayedSession?.Evaluating != true,
                "Stop" => state is SessionState.Running or SessionState.Debugging or SessionState.NestedPrompt,
                "StepInto" or "StepOver" or "StepOut" or "Continue" => paused && displayedSession?.Evaluating != true,
                "BreakAll" => state == SessionState.Running,
                "Breakpoint" or "RemoveBreakpoints" or "NewBreakpoint" => (ready || paused || state == SessionState.Running) &&
                    displayedSession?.Evaluating != true && displayedSession?.EditingBreakpoints != true,
                "Complete" => (ready || paused || nested) && displayedSession?.Evaluating != true,
                "Profiles" or "ShowCommand" => ready,
                "OpenRemoteFile" => (ready || paused) && displayedSession?.Engine.IsRemote == true,
                "ExitRemoteSession" => ready && displayedSession?.Engine.IsRunspacePushed == true,
                "ExecutionPolicy" => ready && OperatingSystem.IsWindows(),
                "Snippets" or "CreateSnippet" => displayedFile is not null && !paused,
                "Fold" => settings.ShowOutlining,
                "Close" or "CloseSession" => displayedSession is not null,
                "Save" or "SaveAs" or "Print" => displayedFile is not null,
                "Encoding" => displayedFile is not null && ready && !closingInProgress,
                "Reload" => displayedFile?.File.Path is not null && ready && !closingInProgress,
                "Undo" or "Redo" or "Cut" or "Copy" or "Paste" or "SelectAll" => CanEdit(action),
                "Replace" => !paused,
                _ => true
            };
        }
    }

    private bool CanEdit(string action)
    {
        var target = editTarget ?? ScriptEditor;
        if (target is TextBox box)
            return action switch
            {
                "Undo" => !box.IsReadOnly && box.CanUndo,
                "Redo" => !box.IsReadOnly && box.CanRedo,
                "Copy" => box.SelectionStart != box.SelectionEnd,
                "Cut" => !box.IsReadOnly && box.SelectionStart != box.SelectionEnd,
                "SelectAll" => !string.IsNullOrEmpty(box.Text),
                "Paste" => !box.IsReadOnly && box.IsEffectivelyEnabled,
                _ => false
            };
        if (target is TextEditor editor)
            return action switch
            {
                "Undo" => !editor.IsReadOnly && editor.Document.UndoStack.CanUndo,
                "Redo" => !editor.IsReadOnly && editor.Document.UndoStack.CanRedo,
                "Copy" => editor.SelectionLength > 0 || (editor.Options.CutCopyWholeLine && editor.Document.TextLength > 0),
                "Cut" => !editor.IsReadOnly && (editor.SelectionLength > 0
                    ? editor.TextArea.ReadOnlySectionProvider.GetDeletableSegments(new SimpleSegment(editor.SelectionStart, editor.SelectionLength)).Any()
                    : editor.Options.CutCopyWholeLine && editor.TextArea.ReadOnlySectionProvider.CanInsert(editor.CaretOffset)),
                "SelectAll" => editor.Document.TextLength > 0,
                "Paste" => !editor.IsReadOnly && editor.TextArea.ReadOnlySectionProvider.CanInsert(editor.CaretOffset),
                _ => false
            };
        return false;
    }

    private async Task RecoverScriptsAsync()
    {
        if (recovering) return;
        recovering = true;
        try
        {
            var scripts = await recovery.ReadAsync();
            if (scripts.Count == 0 || Workbench.SelectedSession is not { } session) return;
            var answer = await Dialogs.ChooseAsync(this, "Recover scripts", $"Recover {scripts.Count} autosaved script(s) from a previous session?",
                "Recover", "Discard", "Cancel");
            if (answer is null or "Cancel") return;
            foreach (var script in scripts)
            {
                if (answer == "Recover")
                {
                    var owningSession = Workbench.Sessions.FirstOrDefault(candidate => candidate.Name == script.SessionName) ?? session;
                    var target = restoredRecoveryTargets.GetValueOrDefault(script.Id, (owningSession, null));
                    if (target.Placeholder is null && script.Path is null)
                        target.Placeholder = target.Session.Files.FirstOrDefault(tab =>
                            tab.File.Path is null && tab.File.Name == script.Name && tab.Document.TextLength == 0);
                    var file = ScriptFile.FromRecovery(script.Name, script.Text, script.Encoding);
                    var tab = new ScriptTab(file);
                    if (target.Placeholder is not null) target.Session.Files.Remove(target.Placeholder);
                    target.Session.Files.Add(tab);
                    target.Session.SelectedFile = tab;
                    Workbench.SelectedSession = target.Session;
                    await recovery.SaveAsync(tab.RecoveryId, tab.File, target.Session.Name);
                }

                recovery.Remove(script.Id);
            }
            DisplaySession();
            DisplayFile();
        }
        finally { recovering = false; }
    }

    private async Task AutoSaveAsync()
    {
        autoSaving = true;
        try
        {
            await GuardAsync(async () =>
            {
                foreach (var session in Workbench.Sessions.ToArray())
                    foreach (var tab in session.Files.ToArray())
                        await recovery.SaveAsync(tab.RecoveryId, tab.File, session.Name);
            });
        }
        finally { autoSaving = false; }
    }

    private Task ShowCompletionAsync() => RequestCompletionAsync(editTarget == ConsoleEditor ? ConsoleEditor : ScriptEditor);

    private void ScheduleCompletion(TextEditor editor, string? entered)
    {
        completionTimer.Stop();
        if (entered?.Length != 1 ||
            !(editor == ScriptEditor ? settings.ScriptIntelliSense : settings.ConsoleIntelliSense)) return;
        var start = editor == ConsoleEditor ? displayedSession?.Console.InputStart ?? editor.Document.TextLength : 0;
        if (editor.CaretOffset < start) return;
        var filter = EditorAnalysis.CompletionFilter(editor.Document.GetText(start, editor.Document.TextLength - start), editor.CaretOffset - start);
        if (filter is null) return;
        completion?.Close();
        automaticCompletionEditor = editor;
        automaticCompletionFilter = filter;
        completionTimer.Start();
    }

    private async Task<CompletionSet?> GetCompletionAsync(SessionModel session, string text, int caret)
    {
        completionNotice = null;
        completionPending = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.IntelliSenseTimeoutSeconds));
        try { return await session.Engine.CompleteAsync(text, caret, timeout.Token); }
        catch (InvalidOperationException) when (session.Engine.State != SessionState.Ready && !session.Engine.IsDebuggerPaused && !session.Engine.IsNestedPromptActive)
        { return null; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (displayedSession == session)
            {
                completionNotice = UiText.Get("CompletionTimedOut");
                StatusText.Text = completionNotice;
            }
            System.Diagnostics.Trace.TraceWarning("PowerShell completion timed out.");
            return null;
        }
        finally { completionPending = false; }
    }

    private async Task RequestCompletionAsync(TextEditor editor, IReadOnlySet<CompletionResultType>? filter = null)
    {
        if (displayedSession is not { } session || (session.Engine.State != SessionState.Ready && !session.Engine.IsDebuggerPaused && !session.Engine.IsNestedPromptActive) ||
            session.Evaluating || completion is not null || completionPending ||
            editor.IsReadOnly && !(editor == ScriptEditor && session.Engine.IsDebuggerPaused)) return;
        var revision = session.DebugRevisionCounter;
        var document = editor.Document;
        var start = editor == ConsoleEditor ? session.Console.InputStart : 0;
        if (editor.CaretOffset < start) return;
        var snapshot = document.Text;
        var text = snapshot[start..];
        var caret = editor.CaretOffset - start;
        var results = await GetCompletionAsync(session, text, caret);
        if (results is null || displayedSession != session || revision != session.DebugRevisionCounter || editor.Document != document ||
            document.Text != snapshot || editor.CaretOffset != start + caret || editTarget is not null && editTarget != editor) return;
        var matches = results.Matches.Where(m => filter is null || filter.Contains(m.ResultType)).ToArray();
        if (matches.Length == 0) return;
        var popup = new CompletionWindow(editor.TextArea)
        {
            StartOffset = start + results.Start,
            EndOffset = start + results.Start + results.Length,
            CloseWhenCaretAtBeginning = false
        };
        completion = popup;
        foreach (var match in matches) popup.CompletionList.CompletionData.Add(new PowerShellCompletion(match));
        popup.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !(editor == ScriptEditor ? settings.ScriptCompletionOnEnter : settings.ConsoleCompletionOnEnter))
            { popup.Close(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        popup.Closed += (_, _) => { if (completion == popup) completion = null; };
        popup.Show();
        popup.CompletionList.SelectItem(text.Substring(results.Start, caret - results.Start));
        if (filter?.Contains(CompletionResultType.ProviderContainer) == true && results.Length == 0)
            popup.CompletionList.SelectedItem = null;
    }

    private void ApplySettings()
    {
        CapturePaneGeometry();
        ScriptEditor.ShowLineNumbers = settings.ShowLineNumbers;
        ScriptEditor.WordWrap = settings.WordWrap;
        if (!settings.ScriptIntelliSense) completion?.Close();
        if (!settings.ConsoleIntelliSense && completion?.TextArea == ConsoleEditor.TextArea) completion.Close();
        WorkbenchToolbar.IsVisible = settings.ShowToolbar;
        autoSaveTimer.Stop();
        if (settings.AutoSaveMinutes > 0 && initialized)
        {
            autoSaveTimer.Interval = TimeSpan.FromMinutes(settings.AutoSaveMinutes);
            autoSaveTimer.Start();
        }
        AnalyzeScript();
        ApplyAppearance();
        ZoomSlider.Value = settings.Zoom;
        SetZoom(settings.Zoom);
        ApplySidePaneLayout();
        var right = settings.Layout == "Right";
        var maximized = settings.Layout == "Maximized";
        var ratio = right ? settings.Geometry.RightScriptRatio : settings.Geometry.TopScriptRatio;
        PaneGrid.RowDefinitions = right || maximized ? new RowDefinitions("*") : new RowDefinitions
        {
            new() { Height = new GridLength(ratio, GridUnitType.Star) },
            new() { Height = new GridLength(5) },
            new() { Height = new GridLength(1 - ratio, GridUnitType.Star) }
        };
        PaneGrid.ColumnDefinitions = !right ? new ColumnDefinitions("*") : new ColumnDefinitions
        {
            new() { Width = new GridLength(1 - ratio, GridUnitType.Star) },
            new() { Width = new GridLength(5) },
            new() { Width = new GridLength(ratio, GridUnitType.Star) }
        };
        appliedPaneLayout = settings.Layout;
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
        UpdateMenuState();
    }

    private void ApplySidePaneLayout()
    {
        var showDebugger = displayedSession?.DebuggerPaneVisible == true;
        var showCommands = settings.ShowCommands;
        if (!sidePaneLayoutInitialized || showDebugger != debuggerDockVisible || showCommands != commandDockVisible)
        {
            if (sidePaneLayoutInitialized)
            {
                if (debuggerDockVisible) debuggerDockWidth = OuterGrid.ColumnDefinitions[Grid.GetColumn(DebuggerPane)].Width;
                if (commandDockVisible) commandDockWidth = OuterGrid.ColumnDefinitions[Grid.GetColumn(CommandsPane)].Width;
            }
            Grid.SetColumn(CommandsPane, showDebugger ? 4 : 2);
            Grid.SetColumn(CommandSplitter, showDebugger ? 3 : 1);
            OuterGrid.ColumnDefinitions = new ColumnDefinitions
            {
                new() { Width = new GridLength(1, GridUnitType.Star) },
                new() { Width = new GridLength(showDebugger || showCommands ? 5 : 0) },
                new() { Width = showDebugger ? debuggerDockWidth : showCommands ? commandDockWidth : new GridLength(0), MinWidth = showDebugger ? 340 : 0 },
                new() { Width = new GridLength(showDebugger && showCommands ? 5 : 0) },
                new() { Width = showDebugger && showCommands ? commandDockWidth : new GridLength(0) }
            };
            debuggerDockVisible = showDebugger;
            commandDockVisible = showCommands;
            sidePaneLayoutInitialized = true;
        }
        DebuggerPane.IsVisible = DebuggerSplitter.IsVisible = showDebugger;
        CommandsPane.IsVisible = CommandSplitter.IsVisible = showCommands;
    }

    private void OnZoomChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!initialized) return;
        settings.Zoom = e.NewValue;
        SetZoom(e.NewValue);
    }

    private void SetZoom(double percent)
    {
        ScriptEditor.FontSize = ConsoleEditor.FontSize = settings.FontSize * 4 / 3 * percent / 100;
        ZoomText.Text = $"{percent:0}%";
    }

    private async Task RefreshCommandsAsync(SessionModel session)
    {
        if (windowClosed || !Workbench.Sessions.Contains(session)) return;
        var version = ++commandRequestVersion;
        var runspaceId = session.Engine.RunspaceId;
        IReadOnlyList<CommandDescription> commands;
        try { commands = await session.Engine.GetCommandsAsync(windowCancellation.Token); }
        catch (OperationCanceledException) when (windowCancellation.IsCancellationRequested) { return; }
        if (windowClosed || !Workbench.Sessions.Contains(session) || session.Engine.State == SessionState.Disposed ||
            runspaceId != session.Engine.RunspaceId) return;
        session.Commands = commands;
        session.CommandForms.Clear();
        if (session == displayedSession && version == commandRequestVersion) SetCommandModules();
    }

    private void SetCommandModules()
    {
        if (windowClosed) return;
        updatingCommandList = true;
        ModuleFilter.ItemsSource = new[] { "All" }.Concat((displayedSession?.Commands ?? [])
            .Select(c => c.Module).Where(m => m.Length > 0).Distinct().Order());
        ModuleFilter.SelectedIndex = 0;
        updatingCommandList = false;
        FilterCommands();
    }

    private void FilterCommands()
    {
        if (CommandList is null) return;
        var name = CommandSearch.Text ?? "";
        var module = ModuleFilter.SelectedItem as string;
        updatingCommandList = true;
        CommandList.ItemsSource = displayedSession?.Commands.Where(c =>
            c.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && (module is null or "All" || c.Module == module)).ToArray();
        CommandList.SelectedItem = CommandList.ItemsSource?.Cast<CommandDescription>()
            .FirstOrDefault(c => c.Name == displayedSession?.SelectedCommand);
        updatingCommandList = false;
        SelectCommand();
    }

    private void OnCommandFilterChanged(object? sender, SelectionChangedEventArgs e) { if (!updatingCommandList) FilterCommands(); }
    private void OnCommandSearchChanged(object? sender, TextChangedEventArgs e) => FilterCommands();
    private void OnCommandSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingCommandList) return;
        SelectCommand();
    }
    private async void SelectCommand()
    {
        if (windowClosed) return;
        await GuardAsync(LoadCommandFormAsync);
    }
    private async Task LoadCommandFormAsync()
    {
        if (windowClosed) return;
        commandFormCancellation?.Cancel();
        commandFormCancellation?.Dispose();
        var cancellation = commandFormCancellation = CancellationTokenSource.CreateLinkedTokenSource(windowCancellation.Token);
        var session = displayedSession;
        var runspaceId = session?.Engine.RunspaceId;
        var command = CommandList.SelectedItem as CommandDescription;
        if (session is not null) session.SelectedCommand = command?.Name;
        CommandForm.ShowMessage(UiText.Get(command is null ? "SelectCommand" : "LoadingCommand"));
        if (session is null || command is null) return;
        try
        {
            if (!session.CommandForms.TryGetValue(command.Name, out var form))
            {
                var description = await session.Engine.GetCommandFormAsync(command.Name, command.Module, cancellation.Token);
                form = new(description);
                if (windowClosed || cancellation.IsCancellationRequested || session.Engine.RunspaceId != runspaceId) return;
                session.CommandForms[command.Name] = form;
            }
            if (!cancellation.IsCancellationRequested && displayedSession == session && ReferenceEquals(CommandList.SelectedItem, command))
                CommandForm.ShowCommand(form);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidOperationException or RuntimeException)
        {
            if (!cancellation.IsCancellationRequested && displayedSession == session && ReferenceEquals(CommandList.SelectedItem, command))
            {
                CommandForm.ShowMessage(UiText.Get("CommandFormFailed") + ": " + exception.Message);
                throw;
            }
            System.Diagnostics.Trace.TraceError("Superseded command metadata request failed: {0}", exception);
        }
    }
    private async void OnInsertCommand(object? sender, TappedEventArgs e) =>
        await GuardAsync(() => { InsertCommand(); return Task.CompletedTask; });
    private void InsertCommand()
    {
        if (displayedFile is null || ScriptEditor.IsReadOnly)
            throw new InvalidOperationException(UiText.Get("ReadOnlyReplace"));
        InsertCommandText(CommandForm.GetCommand());
    }

    private void InsertCommandText(string script)
    {
        ScriptEditor.Document.Replace(ScriptEditor.SelectionStart, ScriptEditor.SelectionLength, script + " ");
        ScriptEditor.TextArea.Focus();
    }

    private async Task ShowCommandAsync()
    {
        if (displayedSession is not { } session) return;
        var editor = editTarget as TextEditor ?? ScriptEditor;
        var name = editor.SelectedText.Length > 0
            ? EditorAnalysis.CommandNameAtCaret(editor.SelectedText, 0) ?? editor.SelectedText.Trim()
            : EditorAnalysis.CommandNameAtCaret(editor.Text, editor.CaretOffset);
        if (string.IsNullOrWhiteSpace(name))
            name = await Dialogs.AskAsync(this, UiText.Get("ShowCommand").Replace("_", "").TrimEnd('.'), UiText.Get("CommandName"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var description = await session.Engine.GetCommandFormAsync(name);
        var window = new ShowCommandWindow(new Core.CommandForm(description), displayedFile is not null && !ScriptEditor.IsReadOnly,
            owner => ShowHelpAsync(description.Name, owner));
        var result = await window.ShowDialog<ShowCommandResult?>(this);
        if (result is null) return;
        if (result.Run)
        {
            await session.Engine.ExecuteAsync(result.Script);
            FlushOutput();
            FocusConsoleInput();
        }
        else InsertCommandText(result.Script);
    }

    private async void ShowConsoleCommand(SessionModel session, ShowCommandRequest request)
    {
        if (request.Response.Task.IsCompleted) return;
        try
        {
            Workbench.SelectedSession = session;
            DisplaySession();
            if (request.Command is null)
            {
                var picker = new ShowCommandPickerWindow(request.Commands);
                CloseWhenRequestCompletes(picker, request);
                request.Response.TrySetResult(await picker.ShowDialog<string?>(this));
                return;
            }
            var window = new ShowCommandWindow(new Core.CommandForm(request.Command),
                displayedFile is not null && !ScriptEditor.IsReadOnly,
                async owner =>
                {
                    if (settings.UseLocalHelp)
                    {
                        var document = request.HelpDocument ?? throw new InvalidOperationException(UiText.Get("HelpDocumentUnavailable"));
                        var helpWindow = new CommandHelpWindow(document, settings.HelpView, SaveHelpViewAsync);
                        CloseWhenRequestCompletes(helpWindow, request);
                        await helpWindow.ShowDialog(owner);
                    }
                    else
                    {
                        var uri = request.HelpUri ?? throw new InvalidOperationException(
                            "This command does not publish an online help URL. Enable local help in Options.");
                        if (!await Launcher.LaunchUriAsync(uri))
                            throw new InvalidOperationException("The system could not open the online help URL.");
                    }
                }, request.PassThru)
            {
                Width = request.Width, Height = request.Height
            };
            CloseWhenRequestCompletes(window, request);
            var result = await window.ShowDialog<ShowCommandResult?>(this);
            if (!request.Response.Task.IsCompleted && result is { Run: false })
                InsertCommandText(result.Script);
            request.Response.TrySetResult(result is { Run: true } ? result.Script : null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException or RuntimeException)
        {
            System.Diagnostics.Trace.TraceError("Console Show-Command failed: {0}", exception);
            request.Response.TrySetException(exception);
        }
    }

    private static void CloseWhenRequestCompletes(Window window, ShowCommandRequest request) =>
        _ = request.Response.Task.ContinueWith(_ => Dispatcher.UIThread.Post(window.Close),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private string CommandAtCaret()
    {
        var offset = ScriptEditor.CaretOffset;
        var token = colorizer.Analysis?.Tokens.FirstOrDefault(t => t.Extent.StartOffset <= offset && t.Extent.EndOffset >= offset);
        return token?.Text ?? "Get-Help";
    }

    private async Task ShowHelpAsync(string command, Window? owner = null)
    {
        if (displayedSession is null) return;
        if (!settings.UseLocalHelp)
        {
            var uri = await displayedSession.Engine.GetHelpUriAsync(command);
            if (uri is null) throw new InvalidOperationException("This command does not publish an online help URL. Enable local help in Options.");
            if (!await Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException("The system could not open the online help URL.");
            return;
        }
        var document = await displayedSession.Engine.GetHelpDocumentAsync(command);
        await new CommandHelpWindow(document, settings.HelpView, SaveHelpViewAsync).ShowDialog(owner ?? this);
    }

    private async Task SaveHelpViewAsync(HelpViewSettings preferences)
    {
        var snapshot = settings.Copy();
        snapshot.HelpView = preferences.Copy();
        snapshot.Normalize();
        await snapshot.SaveAsync(settingsFilePath);
        settings = snapshot;
    }

    private async Task ReplaceAsync()
    {
        if (ScriptEditor.IsReadOnly) throw new InvalidOperationException("Resume or stop debugging before replacing script text.");
        await new ReplaceWindow(ScriptEditor).ShowDialog(this);
        ScriptEditor.TextArea.Focus();
    }

    private void NavigateBrace(bool select)
    {
        var editor = editTarget == ConsoleEditor ? ConsoleEditor : ScriptEditor;
        var start = editor == ConsoleEditor ? displayedSession?.Console.InputStart ?? editor.Document.TextLength : 0;
        if (editor.CaretOffset < start) return;
        var pair = EditorAnalysis.MatchingBrace(editor.Document.GetText(start, editor.Document.TextLength - start), editor.CaretOffset - start);
        if (pair is not { } braces) { StatusText.Text = UiText.Get("NoMatchingBrace"); return; }
        var relativeCaret = editor.CaretOffset - start;
        var atOpen = relativeCaret == braces.Open || relativeCaret != braces.Close && relativeCaret - 1 == braces.Open;
        if (editor == ScriptEditor)
            foreach (var fold in folding.AllFoldings.Where(f => f.StartOffset <= start + braces.Close && f.EndOffset >= start + braces.Open))
                fold.IsFolded = false;
        if (select) editor.Select(start + braces.Open, braces.Close - braces.Open + 1);
        else editor.CaretOffset = start + (atOpen ? braces.Close : braces.Open);
        editor.ScrollTo(editor.TextArea.Caret.Line, editor.TextArea.Caret.Column);
        editor.TextArea.Focus();
    }

    private async Task GoToLineAsync()
    {
        var input = await Dialogs.AskAsync(this, "Go to line", $"Line number (1 - {ScriptEditor.Document.LineCount}):");
        if (input is null) return;
        if (!int.TryParse(input, out var line) || line < 1 || line > ScriptEditor.Document.LineCount)
            throw new InvalidOperationException("Enter a valid line number.");
        ScriptEditor.TextArea.Caret.Line = line;
        ScriptEditor.ScrollToLine(line);
        ScriptEditor.TextArea.Focus();
    }

    private async Task InsertSnippetAsync()
    {
        if (ScriptEditor.IsReadOnly || displayedFile is null) return;
        var snippets = await LoadSnippetsAsync();
        var snippet = await new SnippetWindow(snippets).ShowDialog<PowerShellSnippet?>(this);
        if (snippet is null || ScriptEditor.IsReadOnly) return;
        var firstLine = ScriptEditor.Document.GetLineByNumber(1);
        var newLine = firstLine.DelimiterLength == 0 ? Environment.NewLine :
            ScriptEditor.Document.GetText(firstLine.EndOffset, firstLine.DelimiterLength);
        InsertSnippet(ScriptEditor, snippet, newLine);
        ScriptEditor.TextArea.Focus();
    }

    public static void InsertSnippet(TextEditor editor, PowerShellSnippet snippet, string newLine)
    {
        if (editor.IsReadOnly) throw new InvalidOperationException(UiText.Get("ReadOnlyReplace"));
        var offset = editor.SelectionStart;
        var line = editor.Document.GetLineByOffset(offset);
        var prefix = editor.Document.GetText(line.Offset, offset - line.Offset);
        var indentation = new string(prefix.TakeWhile(c => c is ' ' or '\t').ToArray());
        var expanded = snippet.Expand(indentation, newLine);
        editor.Document.Replace(offset, editor.SelectionLength, expanded.Text);
        editor.TextArea.ClearSelection();
        editor.CaretOffset = offset + expanded.Caret;
    }

    private async Task<IReadOnlyList<PowerShellSnippet>> LoadSnippetsAsync(bool includeDefaults = true)
    {
        var loaded = await SnippetCatalog.LoadAsync();
        if (loaded.Errors.Count > 0)
        {
            var message = string.Join(Environment.NewLine, loaded.Errors);
            System.Diagnostics.Trace.TraceError("Could not load snippets: {0}", message);
            await Dialogs.ChooseAsync(this, UiText.Get("SnippetLoadError"), message, UiText.Get("OK"));
        }
        return (includeDefaults && settings.UseDefaultSnippets ? SnippetCatalog.BuiltIns : [])
            .Concat(loaded.Snippets).Distinct().ToArray();
    }

    private async Task CreateSnippetAsync()
    {
        if (ScriptEditor.IsReadOnly || ScriptEditor.SelectionLength == 0)
            throw new InvalidOperationException(UiText.Get("SelectSnippetCode"));
        var code = ScriptEditor.SelectedText;
        var title = await Dialogs.AskAsync(this, UiText.Get("CreateSnippet"), UiText.Get("SnippetTitle"));
        if (title is null) return;
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException(UiText.Get("SnippetTitleRequired"));
        var description = await Dialogs.AskAsync(this, UiText.Get("CreateSnippet"), UiText.Get("SnippetDescription"));
        if (description is null) return;
        var author = await Dialogs.AskAsync(this, UiText.Get("CreateSnippet"), UiText.Get("SnippetAuthor"));
        if (author is null) return;
        await SnippetCatalog.SaveAsync(Path.Combine(SnippetCatalog.UserDirectory, Guid.NewGuid().ToString("N") + ".snippets.ps1xml"),
            [new(title, description, author, code)]);
        StatusText.Text = UiText.Get("SnippetSaved");
    }

    private async Task ImportSnippetsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = UiText.Get("ImportSnippets"), AllowMultiple = true,
            FileTypeFilter = [new("PowerShell snippets") { Patterns = ["*.snippets.ps1xml"] }]
        });
        var imported = new List<PowerShellSnippet>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath() ?? throw new IOException("Only local snippet files are supported.");
            imported.AddRange(SnippetCatalog.Parse(await File.ReadAllTextAsync(path)));
        }
        if (imported.Count == 0) return;
        var existing = await LoadSnippetsAsync(includeDefaults: false);
        var added = imported.Distinct().Except(existing).ToArray();
        if (added.Length > 0)
            await SnippetCatalog.SaveAsync(Path.Combine(SnippetCatalog.UserDirectory, Guid.NewGuid().ToString("N") + ".snippets.ps1xml"), added);
        StatusText.Text = string.Format(UiText.Get("SnippetsImported"), added.Length);
    }

    private async Task ExportSnippetsAsync()
    {
        var snippets = await LoadSnippetsAsync(includeDefaults: false);
        if (snippets.Count == 0) throw new InvalidOperationException(UiText.Get("NoCustomSnippets"));
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = UiText.Get("ExportSnippets"), SuggestedFileName = "Custom.snippets.ps1xml", ShowOverwritePrompt = true,
            FileTypeChoices = [new("PowerShell snippets") { Patterns = ["*.snippets.ps1xml"] }]
        });
        if (file is null) return;
        await SnippetCatalog.SaveAsync(file.TryGetLocalPath() ?? throw new IOException("Only local snippet files are supported."), snippets);
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
        completionTimer.Stop();
        completion?.Close();
        var previousFocus = editTarget;
        var dialog = new OptionsWindow(settings, async updated =>
        {
            CapturePaneGeometry();
            updated.Geometry = settings.Copy().Geometry;
            updated.DebuggerSessions = settings.Copy().DebuggerSessions;
            updated.Normalize();
            await updated.SaveAsync(settingsFilePath);
            settings = updated;
            ApplySettings();
            PopulateRecentMenu();
        });
        await dialog.ShowDialog(this);
        FocusInput(previousFocus);
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.F10 && displayedSession?.Engine.State != SessionState.Debugging)
        {
            WorkbenchMenu.SelectedIndex = 0;
            WorkbenchMenu.Items.OfType<MenuItem>().First().Focus(NavigationMethod.Tab);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab && ctrl)
        {
            var files = Workbench.SelectedSession?.Files;
            if (files is { Count: > 1 })
            {
                var index = files.IndexOf(Workbench.SelectedSession!.SelectedFile!);
                Workbench.SelectedSession.SelectedFile = files[(index + (shift ? files.Count - 1 : 1)) % files.Count];
                DisplayFile(); ScriptEditor.TextArea.Focus();
            }
            e.Handled = true; return;
        }
        if (e.Key == Key.F6)
        {
            var panes = new Control[] { ScriptEditor, ConsoleEditor, CommandSearch, DebuggerTabs }.Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled).ToArray();
            var index = Array.FindIndex(panes, pane => pane.IsKeyboardFocusWithin);
            if (index < 0) index = Array.IndexOf(panes, editTarget);
            FocusInput(panes[(index + (shift ? panes.Length - 1 : 1) + panes.Length) % panes.Length]);
            e.Handled = true; return;
        }
        string? action = e.Key switch
        {
            Key.F5 => shift ? "Stop" : "Run",
            Key.F8 => "RunSelection",
            Key.F9 => ctrl && shift ? "RemoveBreakpoints" : "Breakpoint",
            Key.F10 when displayedSession?.Engine.State == SessionState.Debugging => "StepOver",
            Key.F11 => shift ? "StepOut" : "StepInto",
            Key.F1 => ctrl ? "ShowCommand" : "Help",
            Key.Pause when ctrl => e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? "BreakAll" : "Stop",
            Key.N when ctrl => "New",
            Key.O when ctrl => "Open",
            Key.P when ctrl => "Print",
            Key.S when ctrl => shift ? "SaveAs" : "Save",
            Key.W when ctrl => shift ? "CloseSession" : "Close",
            Key.T when ctrl => "NewSession",
            Key.F when ctrl => "Find",
            Key.H when ctrl => "Replace",
            Key.G when ctrl => "GoToLine",
            Key.OemCloseBrackets when ctrl => shift ? "SelectBrace" : "MatchBrace",
            Key.J when ctrl => "Snippets",
            Key.L when ctrl => "Clear",
            Key.I when ctrl => "FocusScript",
            Key.D when ctrl => "FocusConsole",
            Key.D1 when ctrl => "Top",
            Key.D2 when ctrl => "Right",
            Key.D3 when ctrl => "Maximized",
            Key.C when ctrl && shift => "Commands",
            Key.C when ctrl && displayedSession?.Engine.State is SessionState.Running or SessionState.Debugging or SessionState.NestedPrompt => "Stop",
            Key.Space when ctrl => "Complete",
            Key.OemPlus or Key.Add when ctrl => "ZoomIn",
            Key.OemMinus or Key.Subtract when ctrl => "ZoomOut",
            _ => null
        };
        if (action is null) return;
        e.Handled = true;
        await GuardAsync(() => ActAsync(action));
    }

    private void FocusInput(Control? target)
    {
        if (target is null || !target.IsEffectivelyVisible || !target.IsEffectivelyEnabled) target = ScriptEditor;
        if (target is TextEditor editor) editor.TextArea.Focus();
        else target.Focus();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closingApproved) return;
        e.Cancel = true;
        if (closingInProgress) return;
        closingInProgress = true;
        RefreshState();
        await GuardAsync(async () =>
        {
            foreach (var session in Workbench.Sessions)
            {
                if (session.Engine.State is SessionState.Running or SessionState.Debugging or SessionState.NestedPrompt)
                {
                    if (await Dialogs.ChooseAsync(this, "Stop execution", "Stop the running command and exit?", "Stop", "Cancel") != "Stop")
                        return;
                    await session.Engine.StopAsync();
                }
                foreach (var tab in session.Files)
                    if (!await ConfirmSaveAsync(tab)) return;
                if (session.Engine.State == SessionState.Ready) await RefreshDebuggerAsync(session, reconcile: true);
                await SaveDebuggerSettingsAsync(session);
            }
            await autoSaveTask;
            CapturePaneGeometry();
            CaptureWindowGeometry();
            await settings.SaveAsync(settingsFilePath);
            await SaveWorkbenchAsync(released: true);
            foreach (var session in Workbench.Sessions)
            {
                foreach (var tab in session.Files) recovery.Remove(tab.RecoveryId);
                await session.Engine.DisposeAsync();
            }
            closingApproved = true;
            Close();
        });
        closingInProgress = false;
        if (!windowClosed) RefreshState();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or RuntimeException or JsonException or NotSupportedException or System.Text.DecoderFallbackException or System.Xml.XmlException)
        {
            await ReportErrorAsync("Operation failed", exception);
        }
    }

    private async Task ReportErrorAsync(string title, Exception exception)
    {
        System.Diagnostics.Trace.TraceError("{0}: {1}", title, exception);
        if (windowClosed) return;
        StatusText.Text = title + ": " + exception.Message;
        await Dialogs.ChooseAsync(this, title, exception.Message, "OK");
    }
}
