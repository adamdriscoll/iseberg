using System.Management.Automation.Runspaces;
using Avalonia.Threading;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private async Task NewRemoteSessionAsync()
    {
        var connection = await new RemoteConnectionWindow().ShowDialog<RunspaceConnectionInfo?>(HostWindow);
        if (connection is not null) await NewSessionAsync(connection);
    }

    private async Task PickRemoteFileAsync()
    {
        var path = await Dialogs.AskAsync(HostWindow, UiText.Get("OpenRemoteFile"), UiText.Get("RemoteFilePath"));
        if (path is not null) await OpenRemoteFileAsync(path);
    }

    /// <summary>Opens or selects a server-side script in the selected session's current remote runspace.</summary>
    public async Task OpenRemoteFileAsync(string path)
    {
        VerifyAvailable();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var session = Workbench.SelectedSession ?? throw new InvalidOperationException("Select a PowerShell tab.");
        var existing = session.Files.FirstOrDefault(tab => tab.File.RemoteRunspaceId == session.Engine.RunspaceId &&
            string.Equals(tab.File.Path, path, StringComparison.Ordinal));
        if (existing is null)
        {
            var file = await ReadFileAsync(session, path, remote: true);
            if (file is null) return;
            if (windowClosed || closingInProgress) throw new InvalidOperationException("The workbench closed while reading the remote file.");
            existing = session.Files.FirstOrDefault(tab => tab.File.RemoteRunspaceId == file.RemoteRunspaceId &&
                string.Equals(tab.File.Path, file.Path, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new ScriptTab(file);
                existing.ReplaceBreakpoints(session.Breakpoints.Where(breakpoint =>
                    breakpoint.Spec.Kind == BreakpointKind.Line && SameScript(breakpoint.Spec.ScriptPath, file.Path, remote: true))
                    .Select(breakpoint => breakpoint.Spec));
                session.Files.Add(existing);
            }
        }
        session.SelectedFile = existing;
        if (Workbench.SelectedSession == session) DisplayFile();
    }

    private static bool FileInCurrentRunspace(SessionModel session, ScriptTab tab) =>
        tab.File.IsRemote ? tab.File.RemoteRunspaceId == session.Engine.RunspaceId :
        !session.Engine.IsRemote;

    private async Task OpenDebuggerSourceAsync(SessionModel session, string path)
    {
        if (session.Engine.IsRemote) await OpenRemoteFileAsync(path);
        else if (File.Exists(path)) await OpenFileAsync(path);
    }

    private void OnRunspaceChanged(SessionModel session)
    {
        Interlocked.Increment(ref session.DebugRevisionCounter);
        Dispatcher.UIThread.Post(async () =>
        {
            if (windowClosed || !Workbench.Sessions.Contains(session)) return;
            session.PendingRunspaceRefresh = true;
            session.RefreshRunspaceIdentity();
            session.Completion = null;
            session.Commands = [];
            session.CommandForms.Clear();
            session.SelectedCommand = null;
            session.DebugLocation = null;
            session.DebugSnapshot = null;
            session.SelectedDebugFrame = 0;
            session.Breakpoints = [];
            session.Console.HidePrompt();
            if (displayedSession == session)
            {
                completion?.Close();
                SetCommandModules();
                RenderDebugger();
                RefreshState();
            }
            await RefreshChangedRunspaceAsync(session);
        });
    }

    private async Task RefreshChangedRunspaceAsync(SessionModel session)
    {
        if (windowClosed || !session.PendingRunspaceRefresh || !Workbench.Sessions.Contains(session) ||
            session.Engine.State != SessionState.Ready) return;
        session.PendingRunspaceRefresh = false;
        await GuardAsync(async () =>
        {
            await RefreshCommandsAsync(session);
            if (windowClosed || !Workbench.Sessions.Contains(session)) return;
            await RefreshDebuggerAsync(session, reconcile: true);
        });
    }
}
