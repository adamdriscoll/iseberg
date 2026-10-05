using System.Management.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Iseberg.Core;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg;

public sealed partial class MainWindow
{
    private readonly Dictionary<SessionModel, (Action<SessionState> State, Action<DebugLocation?> Stop)> debuggerHandlers = [];

    private void AttachDebugger(SessionModel session)
    {
        Action<SessionState> stateChanged = _ => Dispatcher.UIThread.Post(RefreshState);
        Action<DebugLocation?> stopped = location =>
        {
            var revision = Interlocked.Increment(ref session.DebugRevisionCounter);
            Dispatcher.UIThread.Post(async () =>
            {
                if (revision != session.DebugRevisionCounter) return;
                session.DebugLocation = location;
                session.DebugSnapshot = null;
                if (location is not null && session.Engine.State == SessionState.Debugging)
                {
                    session.DebuggerPaneVisible = true;
                    Workbench.SelectedSession = session;
                    DisplaySession();
                    await GuardAsync(async () =>
                    {
                        if (!string.IsNullOrEmpty(location.ScriptPath) && File.Exists(location.ScriptPath))
                            await OpenFileAsync(location.ScriptPath);
                        if (session == displayedSession && displayedFile is not null && location.Line > 0 &&
                            location.Line <= ScriptEditor.Document.LineCount)
                        {
                            ScriptEditor.ScrollToLine(location.Line);
                            ScriptEditor.TextArea.Caret.Line = location.Line;
                        }
                        await RefreshDebuggerAsync(session, reconcile: true);
                    });
                }
                RenderDebugger();
                ScriptEditor.TextArea.TextView.Redraw();
                RefreshState();
            });
        };
        debuggerHandlers.Add(session, (stateChanged, stopped));
        session.Engine.StateChanged += stateChanged;
        session.Engine.DebuggerStopped += stopped;
    }

    private void DetachDebugger(SessionModel session)
    {
        if (!debuggerHandlers.Remove(session, out var handlers)) return;
        session.Engine.StateChanged -= handlers.State;
        session.Engine.DebuggerStopped -= handlers.Stop;
    }

    private void RenderDebugger()
    {
        var session = displayedSession;
        DebuggerPane.IsVisible = session?.DebuggerPaneVisible == true;
        VariablesList.ItemsSource = session?.DebugSnapshot?.Variables;
        WatchesList.ItemsSource = session?.DebugSnapshot?.Watches ??
            session?.Watches.Select(expression => new DebugValue(expression, "", "")).ToArray();
        CallStackList.ItemsSource = session?.DebugSnapshot?.CallStack;
        var selectedId = (BreakpointsList.SelectedItem as DebugBreakpoint)?.Id;
        BreakpointsList.ItemsSource = session?.Breakpoints;
        BreakpointsList.SelectedItem = session?.Breakpoints.FirstOrDefault(breakpoint => breakpoint.Id == selectedId);
        UpdateBreakpointControls();
    }

    private async Task RefreshDebuggerAsync(SessionModel session, bool reconcile = false)
    {
        var revision = session.DebugRevisionCounter;
        var state = session.Engine.State;
        if (state != SessionState.Ready && !session.Engine.IsDebuggerPaused) return;
        try
        {
            if (state == SessionState.Ready && !reconcile)
                foreach (var tab in session.Files.Where(tab => tab.File.Path is not null))
                {
                    await session.Engine.SetLineBreakpointsAsync(tab.File.Path!, tab.LineBreakpoints);
                    tab.AcknowledgeBreakpointLines();
                }
            var breakpoints = await session.Engine.GetBreakpointsAsync();
            var snapshot = state == SessionState.Debugging ? await session.Engine.InspectAsync(session.Watches) : null;
            if (revision != session.DebugRevisionCounter || session.Engine.State != state) return;
            session.Breakpoints = breakpoints;
            session.DebugSnapshot = snapshot;
            if (reconcile)
                foreach (var tab in session.Files.Where(tab => tab.File.Path is not null && !tab.File.IsDirty))
                    tab.ReplaceBreakpoints(breakpoints.Where(breakpoint => breakpoint.Spec.Kind == BreakpointKind.Line &&
                        SameScript(breakpoint.Spec.ScriptPath, tab.File.Path)).Select(breakpoint => breakpoint.Spec));
            if (session == displayedSession)
            {
                RenderDebugger();
                ScriptEditor.TextArea.TextView.Redraw();
            }
        }
        catch (InvalidOperationException) when (revision != session.DebugRevisionCounter || session.Engine.State != state ||
            state == SessionState.Debugging && !session.Engine.IsDebuggerPaused)
        {
            // A resume/stop invalidates an in-flight inspector request.
        }
    }

    private static bool SameScript(string? first, string? second) =>
        string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task ToggleLineBreakpointAsync(SessionModel session, ScriptTab file)
    {
        if (session.Engine.State is not (SessionState.Ready or SessionState.Debugging))
            throw new InvalidOperationException("Pause or stop execution before changing breakpoints.");
        var previous = file.LineBreakpoints.ToArray();
        file.ToggleBreakpoint(ScriptEditor.TextArea.Caret.Line);
        try
        {
            if (file.File.Path is not null)
            {
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
        ScriptEditor.TextArea.TextView.Redraw();
    }

    private async Task EditBreakpointAsync(SessionModel session, bool create)
    {
        if (session.Engine.State is not (SessionState.Ready or SessionState.Debugging))
            throw new InvalidOperationException("Pause or stop execution before changing breakpoints.");
        var selected = create ? null : BreakpointsList.SelectedItem as DebugBreakpoint;
        if (!create && selected is null) throw new InvalidOperationException("Select a breakpoint to edit.");
        var spec = selected?.Spec ?? new BreakpointSpec(displayedFile?.File.Path is null ? BreakpointKind.Command : BreakpointKind.Line, displayedFile?.File.Path,
            Line: ScriptEditor.TextArea.Caret.Line);
        var edited = await new BreakpointWindow(spec).ShowDialog<BreakpointSpec?>(this);
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
        foreach (var tab in session.Files.Where(tab => tab.File.Path is not null))
        {
            var applies = replacement?.Kind == BreakpointKind.Line && SameScript(replacement.ScriptPath, tab.File.Path);
            if (previous?.Kind == BreakpointKind.Line && SameScript(previous.ScriptPath, tab.File.Path))
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
        var editable = displayedSession?.Engine.State is SessionState.Ready or SessionState.Debugging &&
            displayedSession?.Evaluating != true;
        var selected = BreakpointsList.SelectedItem is DebugBreakpoint;
        EditBreakpointButton.IsEnabled = EnableBreakpointButton.IsEnabled = DeleteBreakpointButton.IsEnabled = editable && selected;
    }

    private void OnBreakpointSelected(object? sender, SelectionChangedEventArgs e) => UpdateBreakpointControls();

    private async void OnDebugFrameSelected(object? sender, TappedEventArgs e)
    {
        if (CallStackList.SelectedItem is not DebugFrame { ScriptPath: not null } frame) return;
        await GuardAsync(async () =>
        {
            await OpenFileAsync(frame.ScriptPath);
            if (frame.Line > 0 && frame.Line <= ScriptEditor.Document.LineCount)
            {
                ScriptEditor.ScrollToLine(frame.Line);
                ScriptEditor.TextArea.Caret.Line = frame.Line;
                ScriptEditor.TextArea.Focus();
            }
        });
    }
}
