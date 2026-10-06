using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private readonly WorkbenchStateStore workbenchStore;
    private readonly SemaphoreSlim workbenchSaveGate = new(1, 1);
    private readonly Dictionary<Guid, (SessionModel Session, ScriptTab? Placeholder)> restoredRecoveryTargets = [];
    private readonly List<string> printPreviews = [];
    private bool restoringWorkbench;
    private string? appliedPaneLayout;

    public async Task SaveWorkbenchAsync(bool released = false)
    {
        if (!hostingOptions.EnablePersistence || restoringWorkbench) return;
        if (displayedFile is not null) displayedFile.File.CaretOffset = ScriptEditor.CaretOffset;
        using var process = Process.GetCurrentProcess();
        var snapshot = new WorkbenchState
        {
            OwnerProcessId = released ? 0 : Environment.ProcessId,
            OwnerStartedUtc = process.StartTime.ToUniversalTime(),
            SelectedSession = Workbench.Sessions.IndexOf(Workbench.SelectedSession!),
            Sessions = Workbench.Sessions.Select(session => new WorkbenchSessionState
            {
                Name = session.Name,
                ShowDebugger = session.DebuggerPaneVisible,
                SelectedDocument = session.Files.Where(tab => !tab.File.IsRemote).ToList().IndexOf(session.SelectedFile!),
                Documents = session.Files.Where(tab => !tab.File.IsRemote).Select(tab => new WorkbenchDocumentState
                {
                    RecoveryId = tab.RecoveryId, Name = tab.File.Name, Path = tab.File.Path,
                    CaretOffset = tab.File.CaretOffset,
                    Encoding = tab.File.Path is null ? tab.File.EncodingChoice : tab.File.SavedEncodingChoice
                }).ToList()
            }).ToList()
        };
        await workbenchSaveGate.WaitAsync();
        try { await workbenchStore.SaveAsync(snapshot); }
        finally { workbenchSaveGate.Release(); }
    }

    public async Task RestoreWorkbenchAsync()
    {
        if (!hostingOptions.EnablePersistence) return;
        var snapshot = await workbenchStore.LoadAsync();
        if (snapshot is null || snapshot.Sessions.Count == 0) return;
        restoringWorkbench = true;
        try
        {
            foreach (var saved in snapshot.Sessions)
            {
                await NewSessionAsync(name: saved.Name, createDocument: false);
                var session = Workbench.SelectedSession!;
                session.DebuggerPaneVisible = saved.ShowDebugger;
                foreach (var document in saved.Documents)
                {
                    ScriptTab? tab = null;
                    try
                    {
                        var file = document.Path is null ? ScriptFile.CreateUntitled(document.Name, document.Encoding) :
                            await ScriptFile.OpenAsync(document.Path, document.Encoding);
                        file.CaretOffset = Math.Min(document.CaretOffset, file.Text.Length);
                        tab = new ScriptTab(file);
                        if (file.Path is not null)
                            tab.ReplaceBreakpoints(session.Breakpoints.Where(breakpoint => breakpoint.Spec.Kind == BreakpointKind.Line &&
                                SameScript(breakpoint.Spec.ScriptPath, file.Path)).Select(breakpoint => breakpoint.Spec));
                        session.Files.Add(tab);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        await ReportErrorAsync(UiText.Get("RestoreDocumentFailed") + ": " + document.Name, exception);
                    }
                    restoredRecoveryTargets[document.RecoveryId] = (session, document.Path is null ? tab : null);
                }
                if (session.Files.Count == 0) NewFile();
                session.SelectedFile = session.Files[Math.Clamp(saved.SelectedDocument, 0, session.Files.Count - 1)];
            }
            Workbench.SelectedSession = Workbench.Sessions[Math.Clamp(snapshot.SelectedSession, 0, Workbench.Sessions.Count - 1)];
            DisplaySession();
            ApplySidePaneLayout();
        }
        finally { restoringWorkbench = false; }
    }

    private void CapturePaneGeometry()
    {
        if (appliedPaneLayout == "Top" && PaneGrid.RowDefinitions.Count == 3)
        {
            var script = PaneGrid.RowDefinitions[0].ActualHeight;
            var console = PaneGrid.RowDefinitions[2].ActualHeight;
            if (script + console > 0) settings.Geometry.TopScriptRatio = script / (script + console);
        }
        else if (appliedPaneLayout == "Right" && PaneGrid.ColumnDefinitions.Count == 3)
        {
            var script = PaneGrid.ColumnDefinitions[2].ActualWidth;
            var console = PaneGrid.ColumnDefinitions[0].ActualWidth;
            if (script + console > 0) settings.Geometry.RightScriptRatio = script / (script + console);
        }
        if (debuggerDockVisible)
        {
            var width = OuterGrid.ColumnDefinitions[Grid.GetColumn(DebuggerPane)].ActualWidth;
            if (width > 0) settings.Geometry.DebuggerWidth = width;
        }
        if (commandDockVisible)
        {
            var width = OuterGrid.ColumnDefinitions[Grid.GetColumn(CommandsPane)].ActualWidth;
            if (width > 0) settings.Geometry.CommandsWidth = width;
        }
    }

    private void CaptureWindowGeometry()
    {
        if (!managesWindow || hostWindow is null) return;
        if (hostWindow.WindowState == WindowState.Normal)
        {
            settings.Geometry.WindowWidth = hostWindow.Width;
            settings.Geometry.WindowHeight = hostWindow.Height;
            settings.Geometry.WindowX = hostWindow.Position.X;
            settings.Geometry.WindowY = hostWindow.Position.Y;
        }
        if (hostWindow.WindowState != WindowState.Minimized) settings.Geometry.Maximized = hostWindow.WindowState == WindowState.Maximized;
    }

    private void ApplyWindowGeometry()
    {
        if (!managesWindow || hostWindow is null) return;
        var window = hostWindow;
        var geometry = settings.Geometry;
        geometry.Normalize();
        var position = geometry.WindowX is { } x && geometry.WindowY is { } y ? new PixelPoint(x, y) : (PixelPoint?)null;
        var screen = position is { } point ? window.Screens.All.FirstOrDefault(screen => screen.WorkingArea.Contains(point)) : null;
        screen ??= window.Screens.Primary;
        window.Width = geometry.WindowWidth;
        window.Height = geometry.WindowHeight;
        if (screen is not null)
        {
            window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, screen.WorkingArea.Width / screen.Scaling));
            window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, screen.WorkingArea.Height / screen.Scaling));
            if (position is { } saved && screen.WorkingArea.Contains(saved))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = new PixelPoint(
                    Math.Clamp(saved.X, screen.WorkingArea.X, Math.Max(screen.WorkingArea.X, screen.WorkingArea.Right - (int)(window.Width * screen.Scaling))),
                    Math.Clamp(saved.Y, screen.WorkingArea.Y, Math.Max(screen.WorkingArea.Y, screen.WorkingArea.Bottom - (int)(window.Height * screen.Scaling))));
            }
            else if (position is not null)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = new PixelPoint(screen.WorkingArea.X, screen.WorkingArea.Y);
            }
        }
        if (geometry.Maximized) window.WindowState = WindowState.Maximized;
    }

    private async Task PrintScriptAsync()
    {
        if (displayedFile is null) return;
        var path = Path.Combine(Path.GetTempPath(), "iseberg-print-" + Guid.NewGuid().ToString("N") + ".html");
        printPreviews.Add(path);
        await ScriptPrint.WriteAsync(path, ScriptPrint.CreateHtml(displayedFile.File.Name, displayedFile.Document.Text,
            UiText.Get("Print"), UiText.Get("PrintHint")));
        if (await Launcher.LaunchFileInfoAsync(new FileInfo(path)) != true)
            throw new IOException(UiText.Get("PrintLaunchFailed"));
        StatusText.Text = UiText.Get("PrintHint");
    }
}
