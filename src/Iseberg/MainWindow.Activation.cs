using Avalonia.Threading;

namespace Iseberg;

public sealed partial class MainWindow
{
    private readonly Queue<string> activatedFiles = new();
    private bool startupComplete;
    private bool openingActivatedFiles;

    public void OpenActivatedFiles(IEnumerable<string> paths)
    {
        Dispatcher.UIThread.Post(async () => await GuardAsync(async () =>
        {
            if (windowClosed || closingInProgress) return;
            foreach (var path in paths) activatedFiles.Enqueue(path);
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
            if (!windowClosed) Activate();
        }
        finally { openingActivatedFiles = false; }
    }
}
