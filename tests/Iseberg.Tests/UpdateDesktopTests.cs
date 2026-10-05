using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class UpdateDesktopTests
{
    [AvaloniaFact]
    public async Task UpdatePromptCanDisableChecksAndManualChecksRemainAvailable()
    {
        var requests = 0;
        using var client = new HttpClient(new UpdateHttpHandler((_, _) =>
        {
            ++requests;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v255.255.65535","assets":[]}""")
            });
        }));
        var directory = Path.Combine(Path.GetTempPath(), "Iseberg-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = Path.Combine(directory, "settings.json");
        await new UserSettings { AutoSaveMinutes = 0 }.SaveAsync(settings);
        var window = new MainWindow([], settingsPath: settings, updateChecker: new(client));
        try
        {
            window.Show();
            await WaitUntilAsync(() => window.OwnedWindows.Any());
            var prompt = window.OwnedWindows.Single();
            Assert.Contains(prompt.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("255.255.65535") == true);
            var disable = prompt.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Content as string == UiText.Get("DisableUpdateChecks"));
            disable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => window.OwnedWindows.Count() == 0);
            await WaitUntilAsync(() => window.GetLogicalDescendants().OfType<MenuItem>()
                .Single(item => item.Tag as string == "CheckForUpdates").IsEnabled);
            Assert.False((await UserSettings.LoadAsync(settings)).CheckForUpdates);
            var manual = window.GetLogicalDescendants().OfType<MenuItem>()
                .Single(item => item.Tag as string == "CheckForUpdates");
            Assert.Equal(UiText.Get("CheckForUpdatesMenu"), manual.Header);
            manual.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntilAsync(() => window.OwnedWindows.Any());
            Assert.Equal(2, requests);
            window.OwnedWindows.Single().Close();
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task PendingUpdateDoesNotBlockStartupOrOpeningAssociatedFilesAndClosingCancelsIt()
    {
        var requested = false;
        var cancelled = false;
        using var client = new HttpClient(new UpdateHttpHandler(async (_, token) =>
        {
            requested = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            throw new InvalidOperationException("Unreachable");
        }));
        var directory = Path.Combine(Path.GetTempPath(), "Iseberg-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = Path.Combine(directory, "settings.json");
        var script = Path.Combine(directory, "associated file.psm1");
        await new UserSettings { AutoSaveMinutes = 0 }.SaveAsync(settings);
        await File.WriteAllTextAsync(script, "# associated file; never executed\n");
        var window = new MainWindow([], settingsPath: settings, updateChecker: new(client));
        try
        {
            window.OpenActivatedFiles([script]);
            window.Show();
            await WaitUntilAsync(() => requested);
            Assert.NotNull(window.Workbench.SelectedSession);
            Assert.Contains(window.Workbench.SelectedSession.Files, file => file.File.Path == script);
            Assert.Equal(Iseberg.Core.SessionState.Ready, window.Workbench.SelectedSession.Engine.State);
            var second = Path.Combine(directory, "second file.psd1");
            await File.WriteAllTextAsync(second, "@{}\n");
            window.OpenActivatedFiles([second]);
            await WaitUntilAsync(() => window.Workbench.SelectedSession.Files.Any(file => file.File.Path == second));
            window.Close();
            await WaitUntilAsync(() => cancelled);
        }
        finally
        {
            window.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task DisabledStartupCheckMakesNoNetworkRequestAndPersists()
    {
        var requests = 0;
        using var client = new HttpClient(new UpdateHttpHandler((_, _) =>
        {
            ++requests;
            throw new HttpRequestException("Offline");
        }));
        var directory = Path.Combine(Path.GetTempPath(), "Iseberg-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = Path.Combine(directory, "settings.json");
        var script = Path.Combine(directory, "startup.ps1");
        await new UserSettings { CheckForUpdates = false, AutoSaveMinutes = 0 }.SaveAsync(settings);
        await File.WriteAllTextAsync(script, "# startup\n");
        var window = new MainWindow([script], settingsPath: settings, updateChecker: new(client));
        try
        {
            window.Show();
            await WaitUntilAsync(() => window.Workbench.SelectedSession?.Files.Any(file => file.File.Path == script) == true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, requests);
            Assert.False((await UserSettings.LoadAsync(settings)).CheckForUpdates);
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task OfflineStartupCheckReportsFailureWithoutAModalDialog()
    {
        using var client = new HttpClient(new UpdateHttpHandler((_, _) => throw new HttpRequestException("Offline")));
        var directory = Path.Combine(Path.GetTempPath(), "Iseberg-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = Path.Combine(directory, "settings.json");
        await new UserSettings { AutoSaveMinutes = 0 }.SaveAsync(settings);
        var window = new MainWindow([], settingsPath: settings, updateChecker: new(client));
        try
        {
            window.Show();
            await WaitUntilAsync(() => window.FindControl<TextBlock>("StatusText")!.Text?.Contains("Offline") == true);
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Offline", window.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Empty(window.OwnedWindows);
            Assert.True(window.FindControl<AccessibleTextEditor>("ScriptEditor")!.IsEnabled);
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, cancellation.Token);
        }
    }
}
