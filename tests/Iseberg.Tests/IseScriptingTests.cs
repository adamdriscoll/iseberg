using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using Iseberg.Core;
using Xunit;
using SessionState = Iseberg.Core.SessionState;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class IseScriptingTests
{
    [AvaloniaFact]
    public async Task ScriptsEditLiveDocumentsWithSelectionCaretUndoAndFileLifetime()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        window.Workbench.Sessions.Add(session);
        window.Workbench.SelectedSession = session;
        window.Show();
        try
        {
            await session.Engine.InitializeAsync();
            var result = await Execute(session, """
                Set-StrictMode -Version Latest
                $file = $psISE.CurrentPowerShellTab.Files.Add()
                $file.Editor.Text = "First`r`nSECOND"
                $file.Editor.Select(2, 1, 2, 7)
                "selected=$($file.Editor.SelectedText)"
                $file.Editor.InsertText('second')
                "caret=$($file.Editor.CaretLine),$($file.Editor.CaretColumn)"
                "lines=$($file.Editor.LineCount)"
                "line=$($file.Editor.CaretLineText)"
                "untitled=$($file.IsUntitled);saved=$($file.IsSaved)"
                """);
            var file = session.Files.Single();
            Assert.Equal("First\r\nsecond", file.Document.Text);
            Assert.Same(file.Document, window.FindEditor("ScriptEditor")!.Document);
            Assert.True(file.Document.UndoStack.CanUndo);
            Assert.Contains("selected=SECOND", session.ConsoleDocument.Text);
            Assert.Contains("caret=2,7", session.ConsoleDocument.Text);
            Assert.Contains("lines=2", session.ConsoleDocument.Text);
            Assert.Contains("line=second", session.ConsoleDocument.Text);
            result = await Execute(session, """
                try { $psISE.CurrentPowerShellTab.Files.Remove($file) } catch { "dirty-rejected" }
                try { $file.Editor.SetCaretPosition(2, 99) } catch { "position-rejected" }
                try { $file.Editor.Select(2, 3, 1, 1) } catch { "range-rejected" }
                $psISE.CurrentPowerShellTab.Files.Remove($file, $true)
                try { $file.Editor.Text = 'cannot edit closed file' } catch { "closed-rejected" }
                """);
            Assert.Empty(session.Files);
            Assert.Contains("dirty-rejected", result);
            Assert.Contains("position-rejected", result);
            Assert.Contains("range-rejected", result);
            Assert.Contains("closed-rejected", result);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task FileSaveUsesExplicitEncodingAndRejectsExternalChangesAndOverwrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-ise-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "saved.ps1");
        var occupied = Path.Combine(directory, "occupied.ps1");
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        window.Workbench.Sessions.Add(session);
        window.Workbench.SelectedSession = session;
        window.Show();
        try
        {
            await session.Engine.InitializeAsync();
            await Execute(session, $"""
                $file = $psISE.CurrentPowerShellTab.Files.Add()
                $file.Editor.Text = 'saved content'
                $file.SaveAs('{Quote(path)}', [System.Text.Encoding]::Unicode)
                "saved=$($file.IsSaved);untitled=$($file.IsUntitled)"
                """);
            Assert.Equal(new ScriptEncoding(1200, true), session.Files.Single().File.EncodingChoice);
            Assert.Equal("saved content", (await ScriptFile.OpenAsync(path)).Text);
            Assert.Contains("saved=True;untitled=False", session.ConsoleDocument.Text);
            await File.WriteAllTextAsync(path, "external content");
            await File.WriteAllTextAsync(occupied, "keep me");
            var result = await Execute(session, $$"""
                $file.Editor.Text = 'changed locally'
                try { $file.Save() } catch { "conflict-rejected" }
                try { $file.SaveAs('{{Quote(occupied)}}') } catch { "overwrite-rejected" }
                try { $psISE.CurrentPowerShellTab.Files.Add('relative.ps1') } catch { "relative-rejected" }
                """);
            Assert.Equal("external content", await File.ReadAllTextAsync(path));
            Assert.Equal("keep me", await File.ReadAllTextAsync(occupied));
            Assert.Contains("conflict-rejected", result);
            Assert.Contains("overwrite-rejected", result);
            Assert.Contains("relative-rejected", result);
            await Execute(session, $"""
                $opened = $psISE.CurrentPowerShellTab.Files.Add('{Quote(occupied)}')
                "opened=$($opened.Editor.Text)"
                $psISE.CurrentPowerShellTab.Files.SetSelectedFile($file)
                """);
            Assert.Contains("opened=keep me", session.ConsoleDocument.Text);
            Assert.Same(session.Files[0], session.SelectedFile);
        }
        finally
        {
            await session.Engine.DisposeAsync();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task NestedMenusAndShortcutsExecuteOriginalBlocksInTheirOwningRunspace()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var first = new SessionModel("PowerShell 1");
        var second = new SessionModel("PowerShell 2");
        window.Workbench.Sessions.Add(first);
        window.Workbench.Sessions.Add(second);
        window.Workbench.SelectedSession = first;
        window.Show();
        try
        {
            await first.Engine.InitializeAsync();
            await second.Engine.InitializeAsync();
            var result = await Execute(first, """
                $value = 'first-session'
                $parent = $psISE.CurrentPowerShellTab.AddOnsMenu.Submenus.Add('My tools', $null, $null)
                $null = $parent.Submenus.Add('_Run', { $global:menuValue = $value; Write-Host "menu=$value" }, 'Alt+P')
                $closureValue = 'captured'
                $closure = { Write-Host "closure=$closureValue" }.GetNewClosure()
                $null = $parent.Submenus.Add('Closure', $closure, $null)
                try { $parent.Submenus.Add('Conflict', {}, 'F5') } catch { "shortcut-rejected" }
                try { $parent.Submenus.Add('Duplicate', {}, 'Alt+P') } catch { "duplicate-rejected" }
                try { $parent.Submenus.Add('Modified builtin', {}, 'Ctrl+Alt+N') } catch { "modified-builtin-rejected" }
                try { $parent.Submenus.Add('Mac builtin', {}, 'Meta+N') } catch { "mac-builtin-rejected" }
                """);
            var addons = window.FindControl<MenuItem>("AddonsMenu")!;
            var parent = addons.Items.OfType<MenuItem>().Single(item => item.Header as string == "My tools");
            var menu = parent.Items.OfType<MenuItem>().First();
            await WaitFor(() => menu.IsEnabled);
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitFor(() => first.Engine.State == SessionState.Ready && Contains(first, "menu=first-session"));
            await Execute(first, "'persistent=' + $menuValue");
            Assert.Contains("persistent=first-session", first.ConsoleDocument.Text);
            parent.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitFor(() => first.Engine.State == SessionState.Ready && Contains(first, "closure=captured"));
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.P, KeyModifiers = KeyModifiers.Alt });
            await WaitFor(() => first.Engine.State == SessionState.Ready && Contains(first, "menu=first-session"));
            Assert.Contains("shortcut-rejected", result);
            Assert.Contains("duplicate-rejected", result);
            Assert.Contains("modified-builtin-rejected", result);
            Assert.Contains("mac-builtin-rejected", result);
            window.Scripting.PowerShellTabs.SetSelectedPowerShellTab(window.Scripting.PowerShellTabs[1]);
            Assert.DoesNotContain(addons.Items.OfType<MenuItem>(), item => item.Header as string == "My tools");
            await Execute(second, "if ($null -eq $menuValue) { 'isolated' }; $psISE.PowerShellTabs.Count");
            Assert.Contains("isolated", second.ConsoleDocument.Text);
            Assert.Contains("2", second.ConsoleDocument.Text);
            window.Scripting.PowerShellTabs.SetSelectedPowerShellTab(window.Scripting.PowerShellTabs[0]);
            result = await Execute(first, """
                $psISE.CurrentPowerShellTab.AddOnsMenu.Submenus.Clear()
                try { $parent.Submenus.Clear() } catch { "removed-menu-rejected" }
                """);
            Assert.Contains("removed-menu-rejected", result);
            Assert.DoesNotContain(addons.Items.OfType<MenuItem>(), item => item.Header as string == "My tools");
        }
        finally { await first.Engine.DisposeAsync(); await second.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task BusyMenuActionsAreDisabledAndStopPreservesTheRunspace()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        window.Workbench.Sessions.Add(session);
        window.Workbench.SelectedSession = session;
        window.Show();
        try
        {
            await session.Engine.InitializeAsync();
            await Execute(session, """
                $null = $psISE.CurrentPowerShellTab.AddOnsMenu.Submenus.Add('Wait', {
                    $global:preservedMenuValue = 42
                    Write-Host 'menu-started'
                    Start-Sleep -Seconds 30
                }, 'Alt+P')
                """);
            var menu = window.FindControl<MenuItem>("AddonsMenu")!.Items.OfType<MenuItem>()
                .Single(item => item.Header as string == "Wait");
            await WaitFor(() => menu.IsEnabled);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Capture(OutputEntry entry)
            {
                if (entry.Kind != OutputKind.Command && entry.Text.Contains("menu-started", StringComparison.Ordinal))
                    started.TrySetResult();
            }
            session.Engine.Output += Capture;
            try
            {
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await started.Task.WaitAsync(TestTimeouts.PowerShellStartup);
                await WaitFor(() => session.Engine.State == SessionState.Running && !menu.IsEnabled);
            }
            finally { session.Engine.Output -= Capture; }
            await session.Engine.StopAsync();
            await WaitFor(() => session.Engine.State == SessionState.Ready && menu.IsEnabled);
            var output = await Execute(session, "$preservedMenuValue");
            Assert.Contains("42", output);
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task SnippetObjectModelReflectsSessionImportsAndPickerData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-ise-snippets-" + Guid.NewGuid().ToString("N"));
        var import = Path.Combine(directory, "import.snippets.ps1xml");
        await SnippetCatalog.SaveAsync(import, [new("Imported only", "description", "author", "snippet-text")]);
        var window = new MainWindow([], initializeOnOpen: false, preferences: new UserSettings { UseDefaultSnippets = false });
        var first = new SessionModel("PowerShell 1", Path.Combine(directory, "user"));
        var second = new SessionModel("PowerShell 2", Path.Combine(directory, "user"));
        window.Workbench.Sessions.Add(first);
        window.Workbench.Sessions.Add(second);
        window.Workbench.SelectedSession = first;
        window.Show();
        try
        {
            await first.Engine.InitializeAsync();
            await second.Engine.InitializeAsync();
            var output = await Execute(first, $$"""
                $psISE.CurrentPowerShellTab.Snippets.Load('{{Quote(import)}}')
                $snippet = $psISE.CurrentPowerShellTab.Snippets[0]
                "title=$($snippet.DisplayTitle);text=$($snippet.Text)"
                "count=$($psISE.CurrentPowerShellTab.Snippets.Count)"
                """);
            Assert.Contains("title=Imported only;text=snippet-text", output);
            Assert.Contains("count=1", output);
            Assert.Contains((await first.Engine.Snippets.LoadAsync()).Snippets, snippet => snippet.Title == "Imported only");
            window.Scripting.PowerShellTabs.SetSelectedPowerShellTab(window.Scripting.PowerShellTabs[1]);
            output = await Execute(second, "$psISE.CurrentPowerShellTab.Snippets.Count");
            Assert.Equal("0", output.Trim());
        }
        finally
        {
            await first.Engine.DisposeAsync();
            await second.Engine.DisposeAsync();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task UnsupportedWpfOptionsAndCrossTabOperationsReportExplicitErrors()
    {
        var window = new MainWindow([], initializeOnOpen: false);
        var session = new SessionModel("PowerShell 1");
        window.Workbench.Sessions.Add(session);
        window.Workbench.SelectedSession = session;
        window.Show();
        try
        {
            await session.Engine.InitializeAsync();
            var result = await Execute(session, """
                Set-StrictMode -Version Latest
                $ErrorActionPreference = 'Stop'
                try { $psISE.CurrentPowerShellTab.VerticalAddOnTools.Add('WPF', [object], $true) } catch { $_.Exception.Message }
                try { $psISE.Options } catch { $_.Exception.Message }
                try { $psISE.PowerShellTabs.Add() } catch { $_.Exception.Message }
                try { $psISE.CurrentPowerShellTab.Invoke('Get-Process') } catch { $_.Exception.Message }
                try { $psISE.CurrentPowerShellTab.ConsolePane } catch { $_.Exception.Message }
                """);
            Assert.Contains("including on Windows", result);
            Assert.Contains("cannot run natively on Linux or macOS", result);
            Assert.Contains("property 'Options' cannot be found", result);
            Assert.Contains("Creating PowerShell tabs from scripts is not supported", result);
            Assert.Contains("Cross-tab script invocation is not supported", result);
            Assert.Contains("property 'ConsolePane' cannot be found", result);
            var editor = window.Scripting.CurrentPowerShellTab.Files.Add().Editor;
            window.Close();
            Assert.Throws<ObjectDisposedException>(() => editor.Text = "closed window");
        }
        finally { await session.Engine.DisposeAsync(); window.Close(); }
    }

    private static string Quote(string text) => text.Replace("'", "''");
    private static bool Contains(SessionModel session, string text)
    {
        session.FlushOutput();
        return session.ConsoleDocument.Text.Contains(text, StringComparison.Ordinal);
    }
    private static async Task<string> Execute(SessionModel session, string script)
    {
        var entries = new ConcurrentQueue<OutputEntry>();
        void Capture(OutputEntry entry) { if (entry.Kind != OutputKind.Command) entries.Enqueue(entry); }
        session.Engine.Output += Capture;
        try
        {
            await session.Engine.ExecuteAsync(script).WaitAsync(TestTimeouts.PowerShellStartup);
            session.FlushOutput();
            Assert.DoesNotContain(entries, entry => entry.Kind == OutputKind.Error);
            return string.Concat(entries.Select(entry => entry.Text));
        }
        finally { session.Engine.Output -= Capture; }
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The ISE scripting UI operation did not complete.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
