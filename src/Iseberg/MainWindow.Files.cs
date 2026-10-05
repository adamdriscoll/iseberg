using System.Text;
using Avalonia.Controls;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class MainWindow
{
    private readonly Dictionary<Guid, string?> notifiedFileVersions = [];
    private bool checkingExternalFiles;

    private async Task<ScriptFile?> ReadFileAsync(SessionModel owner, string path, bool remote, ScriptEncoding? encoding = null)
    {
        try
        {
            return remote ? await owner.Engine.OpenRemoteFileAsync(path, encoding) : await ScriptFile.OpenAsync(path, encoding);
        }
        catch (DecoderFallbackException) when (encoding is null)
        {
            var selection = await new EncodingWindow(new(65001, false), canReload: false, openingFile: true).ShowDialog<EncodingSelection?>(this);
            if (selection is null) return null;
            return remote ? await owner.Engine.OpenRemoteFileAsync(path, selection.Encoding) :
                await ScriptFile.OpenAsync(path, selection.Encoding);
        }
    }

    public async Task<bool> ReloadFileAsync(ScriptTab tab, ScriptEncoding? encoding = null, bool confirm = true)
    {
        var owner = Workbench.Sessions.First(session => session.Files.Contains(tab));
        if (owner.Engine.State is SessionState.Running or SessionState.Debugging)
            throw new InvalidOperationException("Stop execution before reloading a script.");
        if (tab.File.Path is not { } path) throw new InvalidOperationException("Save the script before reloading it.");
        if (tab.File.IsRemote && !FileInCurrentRunspace(owner, tab))
            throw new InvalidOperationException("Reopen the remote document in its owning connection.");
        if (confirm && tab.File.IsDirty &&
            await Dialogs.ChooseAsync(this, UiText.Get("ReloadFile"), string.Format(UiText.Get("ReloadConfirm"), tab.File.Name),
                UiText.Get("ReloadFile"), UiText.Get("Cancel")) != UiText.Get("ReloadFile"))
            return false;
        var text = tab.Document.Text;
        var previousEncoding = tab.File.EncodingChoice;
        var replacement = await ReadFileAsync(owner, path, tab.File.IsRemote, encoding ?? tab.File.EncodingChoice);
        if (replacement is null) return false;
        if (tab.Document.Text != text || tab.File.EncodingChoice != previousEncoding)
            throw new InvalidOperationException("The document was edited while reading the file. Reload was canceled.");
        if (displayedFile == tab) tab.File.CaretOffset = ScriptEditor.CaretOffset;
        tab.Reload(replacement);
        notifiedFileVersions.Remove(tab.RecoveryId);
        await autoSaveTask;
        recovery.Remove(tab.RecoveryId);
        if (displayedFile == tab)
        {
            ScriptEditor.CaretOffset = tab.File.CaretOffset;
            AnalyzeScript();
            RefreshCaret();
        }
        return true;
    }

    private async Task ChooseEncodingAsync()
    {
        if (displayedFile is not { } tab) return;
        var selection = await new EncodingWindow(tab.File.EncodingChoice, tab.File.Path is not null).ShowDialog<EncodingSelection?>(this);
        if (selection is null) return;
        if (selection.Reload) await ReloadFileAsync(tab, selection.Encoding);
        else tab.File.SetEncoding(selection.Encoding);
        RefreshCaret();
    }

    public async Task CheckExternalFilesAsync()
    {
        if (checkingExternalFiles || closingInProgress || restoringWorkbench) return;
        checkingExternalFiles = true;
        try
        {
            foreach (var session in Workbench.Sessions.ToArray())
            {
                if (session.Engine.State is not (SessionState.Ready or SessionState.Starting)) continue;
                foreach (var tab in session.Files.ToArray())
                {
                    if (tab.File.Path is not { } path ||
                        tab.File.IsRemote && (!FileInCurrentRunspace(session, tab) || session.Engine.State != SessionState.Ready)) continue;
                    var version = tab.File.IsRemote ? await session.Engine.GetRemoteFileVersionAsync(path) :
                        await ScriptFile.ReadVersionAsync(path);
                    if (version == tab.File.SavedVersion || notifiedFileVersions.TryGetValue(tab.RecoveryId, out var notified) && notified == version)
                        continue;
                    notifiedFileVersions[tab.RecoveryId] = version;
                    var choice = await Dialogs.ChooseAsync(this, UiText.Get("ExternalChange"),
                        string.Format(UiText.Get("ExternalChangeHint"), path), UiText.Get("ReloadFile"), UiText.Get("KeepEdits"));
                    if (choice == UiText.Get("ReloadFile")) await ReloadFileAsync(tab, confirm: false);
                }
            }
        }
        finally { checkingExternalFiles = false; }
    }

    private async Task<bool> SaveWithConflictCheckAsync(SessionModel owner, ScriptTab tab, string path, bool remote)
    {
        if (remote && tab.File.IsRemote && !FileInCurrentRunspace(owner, tab))
            throw new InvalidOperationException("Reopen the remote document in its owning connection before saving.");
        var version = remote ? await owner.Engine.GetRemoteFileVersionAsync(path) : await ScriptFile.ReadVersionAsync(path);
        var original = remote ? tab.File.IsRemote && string.Equals(tab.File.Path, path, StringComparison.Ordinal) :
            !tab.File.IsRemote && SameScript(tab.File.Path, path);
        if (original ? version != tab.File.SavedVersion : version is not null)
        {
            var choice = await Dialogs.ChooseAsync(this, UiText.Get("ExternalChange"),
                string.Format(UiText.Get("SaveConflictHint"), path),
                original && version is not null ?
                    [UiText.Get("ReloadFile"), UiText.Get("OverwriteFile"), UiText.Get("Cancel")] :
                    [UiText.Get("OverwriteFile"), UiText.Get("Cancel")]);
            if (choice == UiText.Get("ReloadFile"))
            {
                await ReloadFileAsync(tab, confirm: false);
                return false;
            }
            if (choice != UiText.Get("OverwriteFile")) return false;
        }
        if (remote) await owner.Engine.SaveRemoteFileAsync(tab.File, path, version);
        else await tab.File.SaveAsync(path, version);
        notifiedFileVersions.Remove(tab.RecoveryId);
        return true;
    }
}
