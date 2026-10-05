using Avalonia.Controls;
using Avalonia.Threading;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class MainWindow
{
    private async void ShowHostInput(SessionModel session, InputRequest request)
    {
        if (request.Response.Task.IsCompleted) return;
        if (windowClosed || !Workbench.Sessions.Contains(session)) { request.Response.TrySetCanceled(); return; }
        try
        {
            Workbench.SelectedSession = session;
            DisplaySession();
            var window = new HostInputWindow(request);
            CloseWhenTaskCompletes(window, request.Response.Task);
            var answer = await window.ShowDialog<string?>(this);
            if (answer is null) request.Response.TrySetCanceled();
            else request.Response.TrySetResult(answer);
        }
        catch (InvalidOperationException exception)
        {
            System.Diagnostics.Trace.TraceError("PowerShell input failed: {0}", exception);
            request.Response.TrySetException(exception);
        }
    }

    private async void ShowCommandError(SessionModel session, CommandErrorRequest request)
    {
        if (request.Response.Task.IsCompleted) return;
        if (windowClosed || !Workbench.Sessions.Contains(session)) { request.Response.TrySetCanceled(); return; }
        try
        {
            Workbench.SelectedSession = session;
            DisplaySession();
            var window = new Window
            {
                Name = "CommandErrorPopup", Title = UiText.Get("CommandErrorTitle"), Width = 640, Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Content = new TextBox
                {
                    Name = "CommandErrorMessage", Text = request.Message, IsReadOnly = true,
                    AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new(12)
                }
            };
            Dialogs.RegisterNames(window);
            CloseWhenTaskCompletes(window, request.Response.Task);
            await window.ShowDialog(this);
            request.Response.TrySetResult();
        }
        catch (InvalidOperationException exception)
        {
            System.Diagnostics.Trace.TraceError("Command error popup failed: {0}", exception);
            request.Response.TrySetException(exception);
        }
    }

    private static void CloseWhenTaskCompletes(Window window, Task response) =>
        _ = response.ContinueWith(_ => Dispatcher.UIThread.Post(window.Close),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private async Task EvaluateNestedConsoleAsync(SessionModel session, string text)
    {
        // A command can itself enter another prompt; do not block its console while it awaits input.
        await session.Engine.EvaluateNestedAsync(text);
        session.FlushOutput();
        RefreshState();
    }
}
