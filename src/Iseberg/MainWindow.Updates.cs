using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class MainWindow
{
    private static readonly HttpClient updateClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ReleaseUpdateChecker releaseUpdateChecker;
    private bool checkingForUpdates;
    private string? updateNotice;
    public static string ApplicationVersion => typeof(MainWindow).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    private async Task CheckForUpdatesAsync(bool automatic)
    {
        if (checkingForUpdates || windowClosed || closingInProgress || (automatic && !settings.CheckForUpdates)) return;
        checkingForUpdates = true;
        updateNotice = null;
        RefreshState();
        UpdateMenuState();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(windowCancellation.Token);
        try
        {
            var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var runtime = platform + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var update = await releaseUpdateChecker.CheckAsync(ApplicationVersion, runtime, cancellation.Token);
            if (windowClosed || closingInProgress || (automatic && !settings.CheckForUpdates)) return;
            if (update is null)
            {
                if (!automatic)
                    await Dialogs.ChooseAsync(this, UiText.Get("Updates"), UiText.Get("NoUpdateAvailable"), UiText.Get("OK"));
                return;
            }
            var download = UiText.Get(update.HasPackage ? "DownloadUpdate" : "ViewRelease");
            var disable = UiText.Get("DisableUpdateChecks");
            var message = string.Format(UiText.Get(update.HasPackage ? "UpdateAvailable" : "UpdateWithoutPackage"),
                update.Version, ApplicationVersion);
            var choice = await Dialogs.ChooseAsync(this, UiText.Get("Updates"), message, download, UiText.Get("Later"), disable);
            if (windowClosed || closingInProgress) return;
            if (choice == disable)
            {
                var updated = settings.Copy();
                updated.CheckForUpdates = false;
                await updated.SaveAsync(settingsFilePath);
                settings = updated;
            }
            else if (choice == download && !await Launcher.LaunchUriAsync(update.DownloadUri))
                throw new InvalidOperationException(UiText.Get("UpdateDownloadFailed"));
        }
        catch (OperationCanceledException) when (windowClosed || windowCancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or
            InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            System.Diagnostics.Trace.TraceWarning("Update check failed: {0}", exception);
            if (windowClosed || closingInProgress) return;
            var message = string.Format(UiText.Get("UpdateCheckFailed"), exception.Message);
            if (automatic)
            {
                updateNotice = message;
                RefreshState();
            }
            else await Dialogs.ChooseAsync(this, UiText.Get("Updates"), message, UiText.Get("OK"));
        }
        finally
        {
            checkingForUpdates = false;
            if (!windowClosed) UpdateMenuState();
        }
    }
}
