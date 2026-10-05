using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Devolutions.Terminal;
using Devolutions.Terminal.Connection;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class MainWindow
{
    private readonly Dictionary<SessionModel, TerminalOutputView> terminalConsoles = [];
    private readonly Dictionary<SessionModel, TermControl> nativeTerminals = [];
    private readonly Dictionary<SessionModel, Action<TerminalRequest>> terminalHandlers = [];
    private string? terminalAppearanceKey;

    private bool IsNativeTerminal(TermControl terminal) => nativeTerminals.ContainsValue(terminal);

    private void EnsureConsoleModeCanChange()
    {
        if (nativeTerminals.Count > 0) throw new InvalidOperationException(UiText.Get("TerminalModeBusy"));
    }

    private void AttachTerminalSession(SessionModel session)
    {
        session.SetConsoleMode(settings.ConsoleMode == "Devolutions");
        Action<TerminalRequest> handler = request => Dispatcher.UIThread.Post(() => RunEmbeddedTerminal(session, request));
        terminalHandlers.Add(session, handler);
        if (session.UseDevolutionsConsole) session.Engine.TerminalRequested += handler;
        if (displayedSession == session) BindConsoleDocument(session);
    }

    private void ApplyConsoleMode()
    {
        var useTerminal = settings.ConsoleMode == "Devolutions";
        foreach (var session in Workbench.Sessions)
        {
            session.SetConsoleMode(useTerminal);
            var handler = terminalHandlers[session];
            session.Engine.TerminalRequested -= handler;
            if (useTerminal) session.Engine.TerminalRequested += handler;
        }
        if (displayedSession is { } displayed) BindConsoleDocument(displayed);
        DisplayTerminalConsole();
    }

    private void BindConsoleDocument(SessionModel session)
    {
        if (ConsoleEditor.Document == session.ConsoleDocument) return;
        completion?.Close();
        completionTimer.Stop();
        ConsoleEditor.Document = session.ConsoleDocument;
        ConsoleEditor.TextArea.ReadOnlySectionProvider = session.Console;
        ConsoleEditor.CaretOffset = session.ConsoleDocument.TextLength;
    }

    private TerminalOutputView GetTerminalConsole(SessionModel session)
    {
        if (terminalConsoles.TryGetValue(session, out var existing)) return existing;
        var view = new TerminalOutputView(session, settings);
        var terminal = view.Control;
        terminal.ContextMenu = CreateEditorMenu(terminal);
        terminal.SelectionChanged += (_, _) => UpdateMenuState();
        terminal.InteractionError += (_, error) => Trace.TraceError("Terminal interaction failed: {0}", error);
        terminal.AddHandler(KeyDownEvent, (_, e) =>
        {
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (command && (e.Key is Key.C or Key.A || e.Key == Key.Insert && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
                return;
            if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt ||
                e.Key is Key.PageUp or Key.PageDown) return;
            FocusConsoleInput();
            ConsoleEditor.TextArea.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = KeyDownEvent, Key = e.Key, KeyModifiers = e.KeyModifiers,
                PhysicalKey = e.PhysicalKey, KeySymbol = e.KeySymbol
            });
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        terminal.AddHandler(TextInputEvent, (_, e) =>
        {
            FocusConsoleInput();
            if (!ConsoleEditor.IsReadOnly) ConsoleEditor.TextArea.PerformTextInput(e.Text);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        terminalConsoles.Add(session, view);
        return view;
    }

    private void DisplayTerminalConsole()
    {
        var enabled = settings.ConsoleMode == "Devolutions" && displayedSession is not null;
        TerminalOutputHost.IsVisible = enabled;
        ConsolePane.RowDefinitions = new(enabled ? "*,Auto,Auto" : "0,*,Auto");
        ConsoleEditor.MinHeight = enabled ? ConsoleEditor.FontSize * 2 + 8 : 0;
        ConsoleEditor.MaxHeight = enabled ? Math.Max(100, ConsoleEditor.FontSize * 5 + 8) : double.PositiveInfinity;
        if (!enabled)
        {
            TerminalOutputHost.Content = null;
            ConsoleEditor.IsVisible = true;
            if (editTarget is TermControl) editTarget = ConsoleEditor;
            return;
        }
        var session = displayedSession!;
        if (nativeTerminals.TryGetValue(session, out var native))
        {
            TerminalOutputHost.Content = native;
            ConsoleEditor.IsVisible = false;
        }
        else
        {
            TerminalOutputHost.Content = GetTerminalConsole(session).Control;
            ConsoleEditor.IsVisible = true;
        }
    }

    private void RefreshTerminalAppearance()
    {
        var key = JsonSerializer.Serialize(new
        {
            settings.FontFamily, settings.FontSize, settings.Theme.Colors, DesktopTheme.HighContrast,
            Foreground = DesktopTheme.Brush("WindowTextBrush").ToString(),
            Background = DesktopTheme.Brush("WindowBrush").ToString(),
            Selection = DesktopTheme.Brush("SelectionBrush").ToString()
        });
        if (key == terminalAppearanceKey) return;
        terminalAppearanceKey = key;
        foreach (var session in terminalConsoles.Keys.ToArray())
        {
            var old = terminalConsoles[session];
            terminalConsoles.Remove(session);
            if (editTarget == old.Control) editTarget = ConsoleEditor;
            CloseWhenDetached(old);
            if (TerminalOutputHost.Content == old.Control) TerminalOutputHost.Content = null;
        }

        DisplayTerminalConsole();
    }

    private static void CloseWhenDetached(TerminalOutputView view)
    {
        if (TopLevel.GetTopLevel(view.Control) is null) { ObserveTerminalClose(view.CloseAsync()); return; }
        void Detached(object? sender, Avalonia.VisualTreeAttachmentEventArgs args)
        {
            view.Control.DetachedFromVisualTree -= Detached;
            ObserveTerminalClose(view.CloseAsync());
        }
        view.Control.DetachedFromVisualTree += Detached;
    }

    private void SetTerminalZoom(double percent)
    {
        foreach (var view in terminalConsoles.Values) view.SetZoom(percent);
        foreach (var terminal in nativeTerminals.Values)
            terminal.AdjustFontSize(settings.FontSize * 4 / 3 * percent / 100 - terminal.FontSize);
    }

    private void RemoveTerminalConsole(SessionModel session)
    {
        if (terminalHandlers.Remove(session, out var handler)) session.Engine.TerminalRequested -= handler;
        if (terminalConsoles.Remove(session, out var view))
        {
            CloseWhenDetached(view);
            if (TerminalOutputHost.Content == view.Control) TerminalOutputHost.Content = null;
            if (editTarget == view.Control) editTarget = ConsoleEditor;
        }
    }

    private void CloseTerminalConsoles()
    {
        foreach (var session in terminalHandlers.Keys.ToArray()) RemoveTerminalConsole(session);
    }

    private static async void ObserveTerminalClose(Task close)
    {
        try { await close; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        { Trace.TraceError("Terminal cleanup failed: {0}", exception); }
    }

    private async void RunEmbeddedTerminal(SessionModel session, TerminalRequest request)
    {
        if (windowClosed || !Workbench.Sessions.Contains(session))
        { request.Response.TrySetCanceled(); return; }
        TermControl? terminal = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken, windowCancellation.Token);
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int? completedExitCode = null;
        Exception? failure = null;
        var cancelled = false;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (nativeTerminals.ContainsKey(session)) throw new InvalidOperationException("This tab already has an interactive terminal application.");
            Workbench.SelectedSession = session;
            DisplaySession();
            var profile = TerminalOutputView.CreateProfile(settings);
            var decoder = Encoding.UTF8.GetDecoder();
            terminal = new TermControl
            {
                Name = "DevolutionsApplication", AccessibleName = Path.GetFileName(request.Executable),
                ConnectionFactory = _ =>
                {
                    var connection = new EmbeddedTerminalConnection(request);
                    connection.OutputReceived += (_, bytes) =>
                    {
                        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                        var count = decoder.GetChars(bytes.Span, chars, flush: false);
                        session.QueueOutput(new(new string(chars, 0, count)));
                    };
                    connection.Faulted += (_, exception) => result.TrySetException(exception);
                    return connection;
                }
            };
            terminal.SessionExited += (_, exit) =>
            {
                if (exit.Reason == TerminalExitReason.ProcessExited && exit.ExitCode is { } code) result.TrySetResult(code);
                else result.TrySetException(new IOException($"Terminal exited without a process status ({exit.Reason})."));
            };
            terminal.InteractionError += (_, error) => Trace.TraceError("Interactive terminal input failed: {0}", error);
            nativeTerminals.Add(session, terminal);
            DisplayTerminalConsole();
            await terminal.StartAsync(profile, 120, 30);
            terminal.ContextMenu = CreateEditorMenu(terminal);
            terminal.SelectionChanged += (_, _) => UpdateMenuState();
            if (displayedSession == session)
            {
                StatusText.Text = UiText.Get("TerminalRunning");
                terminal.Focus();
            }
            completedExitCode = await result.Task.WaitAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        { cancelled = true; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceError("Embedded terminal failed: {0}", exception);
            failure = exception;
        }
        finally
        {
            if (terminal is not null)
            {
                try { await CloseNativeTerminalAsync(session, terminal); }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                { Trace.TraceError("Interactive terminal cleanup failed: {0}", exception); failure ??= exception; }
            }
            nativeTerminals.Remove(session);
            if (cancelled) request.Response.TrySetCanceled();
            else if (failure is not null) request.Response.TrySetException(failure);
            else if (completedExitCode is { } exitCode) request.Response.TrySetResult(exitCode);
            else request.Response.TrySetException(new InvalidOperationException("The terminal did not return an exit code."));
            if (!windowClosed && displayedSession == session)
            {
                DisplayTerminalConsole();
                RefreshState();
                FocusConsoleInput();
            }
        }
    }

    private async Task CloseNativeTerminalAsync(SessionModel session, TermControl terminal)
    {
        TaskCompletionSource? detached = null;
        if (TopLevel.GetTopLevel(terminal) is not null)
        {
            detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs args)
            {
                terminal.DetachedFromVisualTree -= OnDetached;
                detached.TrySetResult();
            }
            terminal.DetachedFromVisualTree += OnDetached;
        }
        nativeTerminals.Remove(session);
        if (!windowClosed && displayedSession == session) DisplayTerminalConsole();
        else if (TerminalOutputHost.Content == terminal) TerminalOutputHost.Content = null;
        if (detached is not null) await detached.Task;
        await terminal.CloseAsync();
    }
}
