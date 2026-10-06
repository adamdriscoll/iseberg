using Avalonia.Threading;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private readonly Queue<string> activatedFiles = new();
    private bool startupComplete;
    private bool openingActivatedFiles;

    /// <summary>Queues local file activation on the UI thread, including activations received before initialization.</summary>
    public void OpenActivatedFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = paths.ToArray();
        Dispatcher.UIThread.Post(async () => await GuardAsync(async () =>
        {
            if (windowClosed || closingInProgress) return;
            foreach (var path in snapshot) activatedFiles.Enqueue(path);
            if (startupComplete) await OpenActivatedFilesAsync();
        }));
    }

    private async Task OpenActivatedFilesAsync()
    {
        if (openingActivatedFiles || windowClosed || closingInProgress) return;
        openingActivatedFiles = true;
        try
        {
            while (activatedFiles.TryDequeue(out var path) && !windowClosed && !closingInProgress)
                await GuardAsync(() => OpenFileAsync(path));
            if (!windowClosed && managesWindow) HostWindow.Activate();
        }
        finally { openingActivatedFiles = false; }
    }
}
