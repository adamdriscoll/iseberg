# NuGet hosting guide

Embed Iseberg's PowerShell workbench in your own Avalonia application, or use its engine without a UI.

For **text editing only in Avalonia 12**, use the separate locally packable `PoshTools.Iseberg.Editor` module described in the [editor hosting guide](editor-hosting.md). It does not own execution, sessions, output or prompts and must not be replaced with this Avalonia 11 workbench package. None of these packages is currently confirmed published on nuget.org; build a local feed before installing.

## Packages and requirements

| Package | Use |
|---|---|
| `PoshTools.ISEBerg` | `WorkbenchControl`, hosting options, observable session/document models, and the standalone `MainWindow`. Depends on `PoshTools.Iseberg.Core`. |
| `PoshTools.Iseberg.Core` | `PowerShellSession`, parser analysis, completion, debugger/remoting operations, script files, and preferences without an Avalonia dependency. |

Both packages target **.NET 10**. The UI uses **Avalonia 11.3.22** and **AvaloniaEdit 11.4.1**; use compatible versions in your host. PowerShell **7.6.6** is supplied through `Microsoft.PowerShell.SDK`, so normal package consumers do not need a separate `pwsh` installation. Native Avalonia prerequisites still apply on Windows, Linux, and macOS.

Install a released version, replacing `1.2.3` below with the version you need:

```powershell
dotnet add package PoshTools.ISEBerg --version 1.2.3
```

The package provides assemblies, not the self-contained desktop installers. Its assembly and C# namespace remain `Iseberg`, so use `using Iseberg;` with the `PoshTools.ISEBerg` package. Do not trim or NativeAOT-publish a host that uses the in-process PowerShell engine: scripts, cmdlets, and modules require dynamic code and reflection.

**This is not a sandbox.** PowerShell runs with the host process's permissions. Hiding a pane does not restrict script capabilities. Profiles remain opt-in; Windows execution policy is respected.

## Embed the workbench

Keep your own Avalonia `Application`, application lifetime, theme, and main window. You do not need to use `Iseberg.App`, call `DesktopTheme.Start`, or register global Iseberg styles. The control and its owned dialogs install their own scoped styles/resources; they do not replace your application resources, title, dimensions, or theme.

Construct a `WorkbenchControl`, place it in a window or layout, and await `InitializeAsync` **after the host window is shown**. Construction does not create a PowerShell runspace or start persistence/update timers.

This window demonstrates explicit initialization, normal close confirmation, forced cleanup, and error reporting:

```csharp
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Iseberg;

public sealed class EditorHostWindow : Window
{
    private readonly WorkbenchControl workbench = new(new WorkbenchOptions
    {
        ShowMenu = true,
        ShowToolbar = true,
        EnableCommandsPane = false,
        EnableDebuggerPane = true
    });
    private bool closeApproved;

    public EditorHostWindow()
    {
        Title = "My PowerShell host";
        Width = 1100;
        Height = 750;
        Content = workbench;
        workbench.CloseRequested += (_, _) => Close();
        workbench.ErrorOccurred += (_, error) => Report(error.Exception);
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += async (_, _) => await DisposeWorkbenchAsync();
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        try { await workbench.InitializeAsync(); }
        catch (Exception exception)
        {
            Report(exception);
            await DisposeWorkbenchAsync();
            closeApproved = true;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (closeApproved) return;
        args.Cancel = true;
        try
        {
            if (!await workbench.RequestCloseAsync()) return;
            closeApproved = true;
            Close();
        }
        catch (Exception exception) { Report(exception); }
    }

    private async Task DisposeWorkbenchAsync()
    {
        try { await workbench.DisposeAsync(); }
        catch (Exception exception) { Report(exception); }
    }

    private void Report(Exception exception)
    {
        Trace.TraceError("Workbench failure: {0}", exception);
        Title = "Workbench error: " + exception.Message;
    }
}
```

In your existing `Application.OnFrameworkInitializationCompleted`, assign this window to `IClassicDesktopStyleApplicationLifetime.MainWindow` as you would any other Avalonia window. Keep your host's normal `AppBuilder.Configure<YourApplication>().UsePlatformDetect()` setup.

You can also put the control in a `Grid`, tab, or other container. Its parameterless constructor supports `<iseberg:WorkbenchControl />` with `xmlns:iseberg="using:Iseberg"` in XAML; resolve that control in your host and initialize it explicitly. Options are construction-time configuration, so create it in C# when customizing them.

## Hosting options

The constructor snapshots `WorkbenchOptions`, including a copy of `Preferences` and `StartupFiles`. Changing the original preferences/list afterwards does not reconfigure an existing control.

| Option | Default | Behavior |
|---|---|---|
| `ShowMenu` | `true` | Shows built-in menus. Hide this when providing your own host commands. |
| `ShowToolbar` | `true` | Allows the toolbar; `Preferences.ShowToolbar` also applies. |
| `ShowStatusBar` | `true` | Shows operation status, caret position, encoding, and zoom. |
| `ShowSessionTabs` | `true` | Allows the tab strip when there are multiple sessions or a remote session. |
| `ShowScriptPane` | `true` | Shows the script editor. |
| `ShowConsolePane` | `true` | Shows the interactive console, subject to the script-maximized layout preference. |
| `EnableCommandsPane` | `true` | Allows the command explorer and initial command discovery. |
| `EnableDebuggerPane` | `true` | Allows debugger inspection panes; the engine's debugger remains available. |
| `CreateInitialSession` | `true` | Initializes one session with an untitled document. Set false to start empty. |
| `EnablePersistence` | `false` | Enables settings, workbench checkpoints, and autosave/recovery. Requires `SettingsPath`. |
| `SettingsPath` | `null` | Absolute host-owned settings file. Do not specify it when persistence is disabled. |
| `SnippetDirectory` | `null` | Optional absolute directory for custom snippets. Otherwise uses the engine's user snippet directory. |
| `EnableUpdateChecks` | `false` | Allows Iseberg release checks, also controlled by `Preferences.CheckForUpdates`. Leave off when the host owns updates. |
| `ShowErrorDialogs` | `false` | Shows modal dialogs for UI-operation failures, in addition to `ErrorOccurred` and tracing. |
| `Preferences` | `null` | Initial `Iseberg.Core.UserSettings`; persisted settings take precedence when persistence is enabled. |
| `StartupFiles` | empty | Local files opened during initialization. Requires `CreateInitialSession`. |

At least one of `ShowScriptPane` or `ShowConsolePane` must be true. A single-pane host uses the full available editor area; disabled panes cannot be brought back by layout shortcuts or saved preferences.

For a console-focused host:

```csharp
var console = new WorkbenchControl(new WorkbenchOptions
{
    ShowScriptPane = false,
    ShowMenu = false,
    ShowToolbar = false,
    EnableCommandsPane = false,
    EnableDebuggerPane = false
});
```

Pane options control presentation, not authorization. Workbench shortcuts operate inside the control; your own host shortcuts outside it remain yours.

## Public workbench operations

Use these methods rather than reaching into named controls or invoking private methods:

| Member | Contract |
|---|---|
| `InitializeAsync()` | Initializes configured state and the initial session. Repeated calls await the same task, including an initialization failure. |
| `IsStarted` | True after successful initialization and until disposal starts. This is distinct from an individual engine's execution state. |
| `Workbench` | Observable sessions, selected session, and per-session documents. Observe collections/properties to update host UI. Prefer the methods below for mutations. |
| `CreateSessionAsync()` | Creates/selects an independent PowerShell session and an untitled document. |
| `SelectSession(session)` | Selects an owned session and updates its console and selected script. |
| `CreateDocument(text = "")` | Creates/selects an untitled document. Nonempty initial text is dirty/unsaved. |
| `SelectDocument(document)` | Selects an owned document and its owning session. |
| `OpenFileAsync(path)` | Opens/selects a local file, preserving encoding checks, duplicate-file warnings, and breakpoint markers. |
| `SaveDocumentAsync(document, path = null)` | Saves an owned document. Supply an absolute local destination to avoid the save picker; remote documents use server paths. Returns false on user cancellation. |
| `CloseDocumentAsync(document)` | Offers the normal dirty-document save prompt. Returns false on cancellation; refuses to close while its session is executing. |
| `CloseSessionAsync(session)` | Offers to stop execution and save dirty documents, then disposes that owned session. Closing the last session creates a replacement session. |
| `RunDocumentAsync(selection = false)` | Runs the selected script using the normal save-before-run and breakpoint behavior; true runs selected text or the current line. |
| `ExecuteAsync(script)` | Executes arbitrary text in the selected session and flushes its console output. Variables/functions persist in that session. |
| `StopAsync()` | Stops the selected session's command, debugger, or nested prompt. |
| `RequestCloseAsync()` | Offers stop/save prompts, persists enabled state, and disposes the workbench if approved. Returns false on cancellation or another close in progress. Does not close the host window. |
| `DisposeAsync()` | Forced cleanup without save prompts. Stops/disposes all owned engines, releases timers/handlers, and retains existing recovery data. Repeated calls await the same result. |
| `CloseRequested` | Exit was selected. The host decides whether to close the control, tab, or containing window. |
| `ErrorOccurred` | A UI or automatic operation failed. Arguments contain `Title` and the original `Exception`; the failure is also traced. |
| `Scripting` | The existing `$psISE` compatibility facade installed in local sessions. Not a separate binary add-on contract. |

Except initialization/disposal and explicitly queued file activation, **call workbench operations on Avalonia's UI thread after successful initialization**. Marshal background callers with `Dispatcher.UIThread.InvokeAsync`. Do not block that thread with `.Wait()` or `.Result`.

Await session/document mutations before starting the next mutation. `StopAsync` and disposal may interrupt ongoing execution. Engine output/state/debug events can arrive from worker threads; marshal your own event handlers before updating host controls.

Public awaited operations propagate validation, I/O, initialization, and lifecycle failures. PowerShell script errors follow PowerShell's stream semantics and appear in `Output`/the console; they do not necessarily fault an execution task. UI actions report failures through `ErrorOccurred`, status text, tracing, and optional modal error dialogs. Save/close prompts return a cancellation result instead of treating cancellation as failure.

For example, after initialization:

```csharp
var script = workbench.CreateDocument("Get-Date");
await workbench.ExecuteAsync("$greeting = 'Hello from the host'; $greeting");

var secondSession = await workbench.CreateSessionAsync();
workbench.SelectDocument(script); // Also reselects the original owning session.
await workbench.SaveDocumentAsync(script, System.IO.Path.GetFullPath("example.ps1"));
await workbench.RunDocumentAsync();
workbench.SelectSession(secondSession);
```

`SessionModel.Engine` exposes the lower-level `PowerShellSession` operations for completion, debugging, and remoting. For instance, `await session.Engine.ConnectAsync(connectionInfo)` connects an owned session, and the workbench reacts to the engine's runspace-change events. The workbench owns those engines: do not remove sessions by mutating `Workbench.Sessions` or dispose engines behind the control's back.

`ScriptTab.Document` is an AvaloniaEdit `TextDocument` with independent undo history and breakpoint anchors; UI-thread edits synchronize to `ScriptTab.File`. Avoid mutating `ScriptTab.File.Text` directly while it belongs to a workbench.

Additional file workflows include `OpenRemoteFileAsync`, `ReloadFileAsync`, and `CheckExternalFilesAsync`. The desktop `$psISE` subset is described in the [ISE compatibility guide](https://github.com/adamdriscoll/iseberg/blob/main/docs/ise-compatibility.md).

## Persistence and lifetime

Embedded controls do not load or save the standalone application's preferences by default. Enable persistence only with a location owned by your host:

```csharp
var settingsPath = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MyApplication", "PowerShellWorkbench", "settings.json");

var workbench = new WorkbenchControl(new WorkbenchOptions
{
    EnablePersistence = true,
    SettingsPath = settingsPath,
    SnippetDirectory = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(settingsPath)!, "snippets")
});
```

Use a distinct settings path for each independently persisted workbench. Settings use that exact file; configuration-only checkpoints use `<SettingsPath>.workbench.json`, and recovery copies use `<SettingsPath>.recovery`. Checkpoints recreate fresh runspaces and reopen local files; they do not persist variables, remote credentials/connections, debugger execution state, or unsaved text.

Your host remains responsible for its window geometry and title. The standalone `MainWindow` alone manages its own window geometry/title, automatic initialization, update checks, and close confirmation.

Always await `RequestCloseAsync` for a normal user-driven close. If it returns false, leave the control attached and usable. Await `DisposeAsync` for forced removal or cleanup after failed initialization; it does not prompt to save and does not create a new recovery copy of current edits. Autosave must already have saved any text you expect to recover.

Temporary visual detachment/reparenting does not dispose the control. Do not permanently remove it without awaiting disposal. A disposed control cannot be restarted; construct a new one.

## Engine-only hosting

Install `PoshTools.Iseberg.Core` when no Avalonia UI is needed. Its assembly name and C# namespace remain `Iseberg.Core`:

```powershell
dotnet add package PoshTools.Iseberg.Core --version 1.2.3
```

```csharp
using Iseberg.Core;

await using var session = new PowerShellSession();
session.Output += entry => Console.Write(entry.Text);
await session.InitializeAsync();
await session.ExecuteAsync("$value = 40; $value + 2");
var completion = await session.CompleteAsync("Get-Pro", 7);
```

The engine serializes runspace operations, supplies parsed completion metadata, and exposes `Output`, `StateChanged`, `InputRequested`, `ProgressChanged`, and debugger/remoting events. An engine-only host must handle `InputRequested`, `ShowCommandRequested`, and `CommandErrorRequested` if its scripts use interactive input or graphical command flows; complete the request's `Response` or stop execution. These events can run on pipeline threads: schedule presentation and return from the handler rather than blocking or re-entering the same engine. Core hosting does not automatically load profiles, configure `$psISE`, or display dialogs.

## Building and publishing

To build packages locally without publishing:

```powershell
dotnet pack Iseberg.slnx -c Release -p:Version=1.2.3 -p:ContinuousIntegrationBuild=true -o publish/nuget
```

This creates `PoshTools.ISEBerg.1.2.3.nupkg` and `PoshTools.Iseberg.Core.1.2.3.nupkg`, with this guide as the package readme and XML documentation alongside the assemblies.

Releases use [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing). Register a nuget.org policy for repository owner `adamdriscoll`, repository `iseberg`, and workflow filename `release.yml`; leave its environment field empty. Choose publishing scopes covering `PoshTools.ISEBerg` and `PoshTools.Iseberg.Core`, including creating new packages for their first release. Add the repository Actions secret **`NUGET_USER`** with your nuget.org profile username (not your email address); no long-lived API key secret is required.

The release publishing job uses GitHub OIDC and `NuGet/login@v1` to obtain a temporary API key immediately before pushing. Release tags supply both package versions, including prerelease suffixes. Publishing pushes Core before the UI package and skips already-published versions; authentication failures fail the job. CI only builds artifacts. See the [publishing setup](https://github.com/adamdriscoll/iseberg/blob/main/docs/development.md#nuget-packages) for the complete trusted-publisher configuration and the [release workflow documentation](https://github.com/adamdriscoll/iseberg/blob/main/docs/development.md#release-workflow) for desktop asset packaging.
