using System.Management.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Iseberg.Core;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private readonly Dictionary<SessionModel, (Action<SessionState> State, Action<DebugLocation?> Stop, Action Runspace)> debuggerHandlers = [];
    private readonly SemaphoreSlim debuggerSettingsGate = new(1, 1);
    private bool renderingDebugger;

    private void AttachDebugger(SessionModel session)
    {
        Action<SessionState> stateChanged = _ => Dispatcher.UIThread.Post(async () =>
        {
            if (windowClosed || !Workbench.Sessions.Contains(session)) return;
            RefreshState();
            await RefreshChangedRunspaceAsync(session);
        });
        Action<DebugLocation?> stopped = location =>
        {
            var revision = Interlocked.Increment(ref session.DebugRevisionCounter);
            Dispatcher.UIThread.Post(async () =>
            {
                if (windowClosed || !Workbench.Sessions.Contains(session)) return;
                if (revision != session.DebugRevisionCounter) return;
                session.DebugLocation = location;
                session.DebugSnapshot = null;
                session.SelectedDebugFrame = 0;
                session.Completion = null;
                if (session == displayedSession) Completion?.Close();
                if (location is not null && session.Engine.State == SessionState.Debugging)
                {
                    session.DebuggerPaneVisible = true;
                    Workbench.SelectedSession = session;
                    DisplaySession();
                    await GuardAsync(async () =>
                    {
                        await RefreshDebuggerAsync(session, reconcile: true);
                        if (revision != session.DebugRevisionCounter || !session.Engine.IsDebuggerPaused) return;
                        if (!string.IsNullOrEmpty(location.ScriptPath))
                            await OpenDebuggerSourceAsync(session, location.ScriptPath);
                        if (session == displayedSession && displayedFile is not null && location.Line > 0 &&
                            location.Line <= ScriptEditor.Document.LineCount)
                        {
                            ScriptEditor.ScrollToLine(location.Line);
                            ScriptEditor.TextArea.Caret.Line = location.Line;
                        }
                    });
                }
                RenderDebugger();
                ScriptEditor.TextArea.TextView.Redraw();
                RefreshState();
            });
        };
        Action changed = () => OnRunspaceChanged(session);
        debuggerHandlers.Add(session, (stateChanged, stopped, changed));
        session.Engine.RunspaceChanged += changed;
        session.Engine.StateChanged += stateChanged;
        session.Engine.DebuggerStopped += stopped;
    }

    private void DetachDebugger(SessionModel session)
    {
        if (!debuggerHandlers.Remove(session, out var handlers)) return;
        session.Engine.StateChanged -= handlers.State;
        session.Engine.DebuggerStopped -= handlers.Stop;
        session.Engine.RunspaceChanged -= handlers.Runspace;
    }

    private void RenderDebugger()
    {
        var session = displayedSession;
        ApplySidePaneLayout();
        VariablesList.ItemsSource = session?.DebugSnapshot?.Variables.Select(value => CreateDebugTreeItem(session, value)).ToArray();
        WatchesList.ItemsSource = (session?.DebugSnapshot?.Watches ??
            session?.Watches.Select(expression => new DebugValue(expression, "", "")).ToArray())?
            .Select(value => CreateDebugTreeItem(session!, value)).ToArray();
        renderingDebugger = true;
        try
        {
            CallStackList.ItemsSource = session?.DebugSnapshot?.CallStack;
            CallStackList.SelectedIndex = session?.DebugSnapshot?.CallStack.Count is > 0 ? session.SelectedDebugFrame : -1;
        }
        finally { renderingDebugger = false; }
        DebuggerScope.Text = session?.DebugSnapshot is { CallStack.Count: > 0 } snapshot
            ? string.Format(UiText.Get("DebuggerInspectionScope"), snapshot.CallStack[session.SelectedDebugFrame].FunctionName)
            : UiText.Get("DebuggerEvaluationScope");
        if (session?.Engine.IsRemote == true)
            DebuggerScope.Text = $"[{session.Engine.RemoteComputerName}] " + DebuggerScope.Text;
        var selectedId = (BreakpointsList.SelectedItem as DebugBreakpoint)?.Id;
        BreakpointsList.ItemsSource = session?.Breakpoints;
        BreakpointsList.SelectedItem = session?.Breakpoints.FirstOrDefault(breakpoint => breakpoint.Id == selectedId);
        UpdateWatchControls();
        UpdateBreakpointControls();
    }

    private TreeViewItem CreateDebugTreeItem(SessionModel session, DebugValue value)
    {
        var item = new TreeViewItem { Header = value.ToString(), Tag = value };
        if (value.Reference is not { } reference) return item;
        item.Items.Add(new TreeViewItem { Header = UiText.Get("ExpandDebuggerValue") });
        var snapshot = session.DebugSnapshot;
        var loading = false;
        var loaded = false;
        async Task LoadAsync(int offset)
        {
            if (loading || session.DebugSnapshot != snapshot || !session.Engine.IsDebuggerPaused) return;
            loading = true;
            if (offset == 0 && item.Items[0] is TreeViewItem placeholder)
                placeholder.Header = UiText.Get("LoadingDebuggerValue");
            try
            {
                var children = await session.Engine.GetValueChildrenAsync(reference, offset);
                if (session.DebugSnapshot != snapshot || !session.Engine.IsDebuggerPaused) return;
                if (offset == 0) item.Items.Clear();
                else item.Items.RemoveAt(item.Items.Count - 1);
                foreach (var child in children.Values) item.Items.Add(CreateDebugTreeItem(session, child));
                if (children.NextOffset is { } next)
                {
                    var more = new Button { Content = UiText.Get("MoreDebuggerValues") };
                    more.Click += async (_, _) => await GuardAsync(() => LoadAsync(next));
                    item.Items.Add(new TreeViewItem { Header = more });
                }
                loaded = true;
            }
            catch (InvalidOperationException) when (session.DebugSnapshot != snapshot || !session.Engine.IsDebuggerPaused) { }
            finally { loading = false; }
        }
        item.PropertyChanged += async (_, e) =>
        {
            if (e.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded && !loaded)
                await GuardAsync(() => LoadAsync(0));
        };
        return item;
    }

    private async Task RefreshDebuggerAsync(SessionModel session, bool reconcile = false)
    {
        await session.DebugRefreshGate.WaitAsync();
        try { await RefreshDebuggerCoreAsync(session, reconcile); }
        finally { session.DebugRefreshGate.Release(); }
    }

    private async Task RefreshDebuggerCoreAsync(SessionModel session, bool reconcile)
    {
        if (windowClosed || !Workbench.Sessions.Contains(session)) return;
        var revision = session.DebugRevisionCounter;
        var state = session.Engine.State;
        if (state != SessionState.Ready && !session.Engine.IsDebuggerPaused) return;
        try
        {
            if (state == SessionState.Ready && !reconcile)
                foreach (var tab in session.Files.Where(tab => tab.File.Path is not null && FileInCurrentRunspace(session, tab)))
                {
                    await session.Engine.SetLineBreakpointsAsync(tab.File.Path!, tab.LineBreakpoints);
                    tab.AcknowledgeBreakpointLines();
                }
            var breakpoints = await session.Engine.GetBreakpointsAsync();
            if (state == SessionState.Debugging)
            {
                session.DebugSnapshot = null;
                if (session == displayedSession) RenderDebugger();
            }
            var snapshot = state == SessionState.Debugging ? await session.Engine.InspectAsync(session.Watches, session.SelectedDebugFrame) : null;
            if (windowClosed || !Workbench.Sessions.Contains(session) ||
                revision != session.DebugRevisionCounter || session.Engine.State != state) return;
            session.Breakpoints = breakpoints;
            session.DebugSnapshot = snapshot;
            if (reconcile)
                foreach (var tab in session.Files.Where(tab => tab.File.Path is not null && !tab.File.IsDirty && FileInCurrentRunspace(session, tab)))
                    tab.ReplaceBreakpoints(breakpoints.Where(breakpoint => breakpoint.Spec.Kind == BreakpointKind.Line &&
                        SameScript(breakpoint.Spec.ScriptPath, tab.File.Path, tab.File.IsRemote)).Select(breakpoint => breakpoint.Spec));
            if (session == displayedSession)
            {
                RenderDebugger();
                ScriptEditor.TextArea.TextView.Redraw();
            }
            await SaveDebuggerSettingsAsync(session);
        }
        catch (InvalidOperationException) when (revision != session.DebugRevisionCounter || session.Engine.State != state ||
            state == SessionState.Debugging && !session.Engine.IsDebuggerPaused)
        {
            // A resume/stop invalidates an in-flight inspector request.
        }
    }

    private static bool SameScript(string? first, string? second, bool remote = false) =>
        string.Equals(first, second, !remote && OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task ToggleLineBreakpointAsync(SessionModel session, ScriptTab file, int? sourceLine = null)
    {
        var line = sourceLine ?? ScriptEditor.TextArea.Caret.Line;
        await PrepareBreakpointEditAsync(session);
        var previous = file.LineBreakpoints.ToArray();
        file.ToggleBreakpoint(line);
        try
        {
            if (file.File.Path is not null)
            {
                if (!FileInCurrentRunspace(session, file)) throw new InvalidOperationException("This file is not in the active runspace. Open the remote copy before setting breakpoints.");
                await session.Engine.SetLineBreakpointsAsync(file.File.Path, file.LineBreakpoints);
                file.AcknowledgeBreakpointLines();
                await RefreshDebuggerAsync(session, reconcile: true);
            }
        }
        catch
        {
            file.ReplaceBreakpoints(previous);
            throw;
        }
        await SaveDebuggerSettingsAsync(session);
        ScriptEditor.TextArea.TextView.Redraw();
        breakpointMargin.InvalidateVisual();
    }

    private async Task EditBreakpointAsync(SessionModel session, bool create)
    {
        var selected = create ? null : BreakpointsList.SelectedItem as DebugBreakpoint;
        if (!create && selected is null) throw new InvalidOperationException("Select a breakpoint to edit.");
        var spec = selected?.Spec ?? new BreakpointSpec(displayedFile?.File.Path is null ? BreakpointKind.Command : BreakpointKind.Line, displayedFile?.File.Path,
            Line: ScriptEditor.TextArea.Caret.Line);
        await PrepareBreakpointEditAsync(session);
        var edited = await new BreakpointWindow(spec).ShowDialog<BreakpointSpec?>(HostWindow);
        if (edited is null) return;
        if (selected is null) await session.Engine.AddBreakpointAsync(edited);
        else await session.Engine.UpdateBreakpointAsync(selected.Id, edited);
        ApplyBreakpointChange(session, selected?.Spec, edited);
        session.DebuggerPaneVisible = true;
        await RefreshDebuggerAsync(session, reconcile: true);
        DebuggerTabs.SelectedIndex = 3;
    }

    private static void ApplyBreakpointChange(SessionModel session, BreakpointSpec? previous, BreakpointSpec? replacement)
    {
        foreach (var tab in session.Files.Where(tab => tab.File.Path is not null && FileInCurrentRunspace(session, tab)))
        {
            var applies = replacement?.Kind == BreakpointKind.Line && SameScript(replacement.ScriptPath, tab.File.Path, tab.File.IsRemote);
            if (previous?.Kind == BreakpointKind.Line && SameScript(previous.ScriptPath, tab.File.Path, tab.File.IsRemote))
                tab.ApplyBreakpointChange(previous, applies ? replacement : null);
            else if (applies && replacement!.Line > 0 && replacement.Line <= tab.Document.LineCount)
                tab.SetBreakpoint(replacement);
        }
    }

    private async Task EvaluateConsoleAsync(SessionModel session, string text)
    {
        session.Evaluating = true;
        RefreshState();
        try { await session.Engine.EvaluateAsync(text); }
        finally
        {
            session.Evaluating = false;
            FlushOutput();
        }
        await RefreshDebuggerAsync(session, reconcile: true);
    }

    private void UpdateBreakpointControls()
    {
        var editable = displayedSession?.Engine.State is SessionState.Ready or SessionState.Debugging or SessionState.Running &&
            displayedSession?.Evaluating != true && displayedSession?.EditingBreakpoints != true;
        var selected = BreakpointsList.SelectedItem is DebugBreakpoint;
        breakpointMargin.IsEnabled = editable && displayedFile is not null;
        NewBreakpointButton.IsEnabled = editable;
        EditBreakpointButton.IsEnabled = EnableBreakpointButton.IsEnabled = DeleteBreakpointButton.IsEnabled = editable && selected;
    }

    private void OnBreakpointSelected(object? sender, SelectionChangedEventArgs e) => UpdateBreakpointControls();

    private void UpdateWatchControls() =>
        RemoveWatchButton.IsEnabled = WatchesList.SelectedItem is TreeViewItem item && WatchesList.Items.Contains(item);

    private void OnWatchSelected(object? sender, SelectionChangedEventArgs e) => UpdateWatchControls();

    private async void OnDebugScopeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (renderingDebugger || displayedSession is not { } session || !session.Engine.IsDebuggerPaused ||
            CallStackList.SelectedItem is not DebugFrame frame || frame.Index == session.SelectedDebugFrame) return;
        if (session.Engine.IsRemote) return;
        var previous = session.SelectedDebugFrame;
        session.SelectedDebugFrame = frame.Index;
        session.Completion = null;
        Completion?.Close();
        Interlocked.Increment(ref session.DebugRevisionCounter);
        await GuardAsync(async () =>
        {
            try { await RefreshDebuggerAsync(session, reconcile: true); }
            catch
            {
                session.SelectedDebugFrame = previous;
                Interlocked.Increment(ref session.DebugRevisionCounter);
                await RefreshDebuggerAsync(session, reconcile: true);
                throw;
            }
        });
    }

    private async void OnDebugFrameSelected(object? sender, TappedEventArgs e)
    {
        if (CallStackList.SelectedItem is not DebugFrame { ScriptPath: not null } frame) return;
        await GuardAsync(async () =>
        {
            if (displayedSession is { } session) await OpenDebuggerSourceAsync(session, frame.ScriptPath);
            if (frame.Line > 0 && frame.Line <= ScriptEditor.Document.LineCount)
            {
                ScriptEditor.ScrollToLine(frame.Line);
                ScriptEditor.TextArea.Caret.Line = frame.Line;
                ScriptEditor.TextArea.Focus();
            }
        });
    }

    private async Task PrepareBreakpointEditAsync(SessionModel session)
    {
        if (session.Evaluating) throw new InvalidOperationException("Wait for debugger evaluation to finish before editing breakpoints.");
        if (session.EditingBreakpoints) throw new InvalidOperationException("A breakpoint edit is already waiting for execution to pause.");
        session.EditingBreakpoints = true;
        RefreshState();
        try
        {
            if (session.Engine.State == SessionState.Running) StatusText.Text = UiText.Get("PauseForBreakpointEdit");
            await session.Engine.PauseForBreakpointEditAsync();
        }
        finally { session.EditingBreakpoints = false; RefreshState(); }
    }

    private async Task SaveDebuggerSettingsAsync(SessionModel session)
    {
        if (session.Engine.IsRunspacePushed) return;
        await debuggerSettingsGate.WaitAsync();
        try
        {
            var saved = new DebuggerSessionSettings
            {
                Name = session.Name,
                Watches = session.Watches.ToList(),
                Breakpoints = session.Breakpoints.Select(breakpoint => breakpoint.Spec).ToList()
            };
            settings.DebuggerSessions.RemoveAll(previous => previous.Name == session.Name);
            settings.DebuggerSessions.Add(saved);
            if (initialized || persistBeforeInitialization) await SaveSettingsAsync();
        }
        finally { debuggerSettingsGate.Release(); }
    }

    private async Task RestoreDebuggerSettingsAsync(SessionModel session)
    {
        var saved = settings.DebuggerSessions.FirstOrDefault(previous => previous.Name == session.Name);
        if (saved is null) return;
        session.Watches.AddRange(saved.Watches);
        foreach (var spec in saved.Breakpoints)
            await session.Engine.AddBreakpointAsync(spec);
        session.Breakpoints = await session.Engine.GetBreakpointsAsync();
        foreach (var tab in session.Files.Where(tab => tab.File.Path is not null))
            tab.ReplaceBreakpoints(session.Breakpoints.Where(breakpoint => breakpoint.Spec.Kind == BreakpointKind.Line &&
                SameScript(breakpoint.Spec.ScriptPath, tab.File.Path)).Select(breakpoint => breakpoint.Spec));
        RenderDebugger();
    }
}
