# Engine-independent editor hosting

`PoshTools.Iseberg.Editor` is a separately packable **.NET 10 / Avalonia 12** text editor, not the Iseberg workbench. Assembly/namespace: `Iseberg.Editor`. It has **no PowerShell runtime/parser dependency**. It never creates/opens a runspace, executes scripts, installs a default session, loads profiles/modules, persists files, checks updates, or owns prompts/output/history/debugging. The host owns those concerns and its `TextDocument`.

This preview is built locally, not published on nuget.org. Iseberg itself now uses this module for its script pane and Options preview. The runtime-owning workbench is still a different hosting choice, not a substitute for editor-only embedding.

## Dependencies and compatibility

Exact direct dependencies: `Avalonia` **12.1.3** and `Avalonia.AvaloniaEdit` **12.0.0**. No System.Management.Automation, Microsoft.PowerShell.SDK, Core/workbench reference, Desktop bootstrap, application theme, modules, or engine-native assets are included. The host's Avalonia Desktop/Fluent/Skia/HarfBuzz dependencies still have native requirements; this is not a claim that an entire desktop host is native-free.

Use a host with Avalonia Desktop and Fluent **12.1.3**. Shared `EditorStyles` loads AvaloniaEdit's existing templates and supplies dynamically resolved, scoped Fluent colors to the editor and its completion popup, never modifying `Application.Resources`. Popup list themes are resolved from the owning editor, including hosts with a control-scoped Fluent theme. NuGet version resolution alone is not binary compatibility evidence. All Iseberg UI projects now use Avalonia 12.1.3 and Edit 12.0.0; Avalonia 11 and Edit 11 are unsupported.

## Local pack and host

```powershell
dotnet restore src\Iseberg.Editor --locked-mode
dotnet pack src\Iseberg.Editor -c Release --no-restore -o publish\editor-feed
# Configure both the local feed and nuget.org in the host's NuGet.Config first.
dotnet add YourHost package PoshTools.Iseberg.Editor --version 0.1.0-preview.1
```

Keep nuget.org configured for dependencies. The [complete example](../examples/EditorHost/README.md) source-maps the unpublished package to a local feed and verifies independent restored package-only consumers. Keep your own `Application`, Desktop lifetime, Fluent theme and normal `AppBuilder`; never initialize `Iseberg.App` or `DesktopTheme`.

```csharp
using Avalonia.Controls;
using AvaloniaEdit.Document;
using Iseberg.Editor;

var document = new TextDocument("Get-Date");
var editor = new PowerShellEditorControl(document);
editor.ErrorOccurred += (_, error) => Report(error.Exception);
var window = new Window { Content = editor };
window.Closed += (_, _) => editor.Dispose();

// A host toolbar action only captures text. Execute through the host's seam.
var snapshot = editor.CaptureText();
// await host.ExecuteScriptAsync(snapshot.Text, ...);
```

## Lexical highlighting versus diagnostics

Highlighting reuses AvaloniaEdit's built-in **PowerShell XSHD definition** without copying its implementation. Each editor loads its own definition/palette, avoiding changes to the global HighlightingManager. This is lexical approximation, not a PowerShell grammar, semantic validation, type resolution, module discovery, execution validation or PSScriptAnalyzer. Nested interpolation, escapes, newer keywords and here-string variants may not match PowerShell's exact grammar. Ordinary, malformed and DSC text can all be edited/rendered without invoking an engine.

**Diagnostics are explicitly unavailable by default**, not "zero syntax errors". `Analysis.State` is `Unavailable` until an explicitly injected host `IEditorAnalysisProvider` returns `Available`. Hosts must display this distinction. Highlighting is independent of diagnostics; stale or unavailable diagnostics never suppress ordinary text editing.

Providers receive immutable full text/version and cancellation. They return `EditorAnalysisResult(version, state, diagnostics)` with `Available` (including an empty list for a genuinely successful host check) or `Unavailable` with an empty list. `Pending` and `Failed` are control-owned states. Diagnostics have typed severity and zero-based UTF-16 spans; EOF spans may have zero length. Results and lists are immutable, validated, and version/lifetime checked.

```csharp
editor.AnalysisProvider = hostAnalysisProvider; // IEditorAnalysisProvider
editor.AnalysisChanged += (_, _) =>
{
    var result = editor.Analysis;
    ShowDiagnosticState(result.State);
    if (result.State == EditorAnalysisState.Available)
        ShowDiagnostics(result.Version, result.Diagnostics);
};
var current = await editor.AnalyzeAsync(cancellationToken);
```

The editor ships **no analysis adapter or parser fallback**. If a host chooses PowerShell's `Parser.ParseInput`, it owns the runtime and all side effects: in PowerShell 7.6.6, parsing DSC `configuration` statements can create/open a runspace, change `DefaultRunspace`, and load DSC keywords; `using assembly` can also consult filesystem/session state. Do not call that unrestricted parser while promising engine-independent analysis. Regex/text guards are not a supported safe mode. Host adapters require their own execution, isolation, platform and redistribution review. The producer does not introduce process isolation or share an administration session.

Iseberg's runtime-owning workbench explicitly supplies its existing PowerShell analysis/completion through host adapters. It extends the shared `TextEditor` with parser-based colors, folding, breakpoint margins and debugger adornments; those execution-aware extensions are not shipped inside the editor-only package. Diagnostics scheduling, cancellation, stale-result checks, find, accessibility and script completion presentation are shared. The protected console uses the same `AccessibleTextEditor`, `EditorStyles` and `EditorCompletionPopup`, while transcript/prompt/history handling remains workbench-owned.

## Interface and ordering

All control operations, document mutations, properties and events use Avalonia's **UI thread**. Marshal background callers with `Dispatcher.UIThread.InvokeAsync`; never block it with `.Wait()` or `.Result`. Providers are called on that thread and must return asynchronously without blocking it. Versions are monotonic **per control**, not global document IDs; do not transfer completion results between controls.

| Member | Contract |
| --- | --- |
| `Document` | Bindable host-owned `TextDocument`; replacement cancels work, resets caret/selection, and advances the version. Never clears/disposes the document or undo stack. |
| `IsReadOnly` | Blocks user/automation/completion edits. The host can still mutate its own document. |
| `CaretOffset`, `Selection`, `Select(EditorTextSpan)` | Validated zero-based UTF-16 positions; attached positions follow AvaloniaEdit's character/newline normalization. |
| `FocusEditor()`, `EditorName` | Focuses the editing area; default accessible name is "PowerShell script editor". |
| `TextEditor` | Advanced host extension seam: the actual `AccessibleTextEditor`, not a second implementation. Configure options, font/brushes, scrolling, margins/renderers, edit commands and protected sections here. Assign documents and read-only state through `PowerShellEditorControl.Document`/`IsReadOnly`, not the child, to preserve lifetime/versioning. |
| `ShowFind()` | Opens/reactivates the shared find panel. |
| `CaptureText(scope)` | Immutable text/version/span. Default whole document; `SelectionOrCurrentLine` captures selection, else current line without its newline. |
| `DocumentVersion`, `Analysis`, `AnalysisChanged` | Versioned diagnostics and explicit `Unavailable/Pending/Available/Failed` state. Edits immediately invalidate attached results; detached host edits are observed on capture/read/attach. |
| `AnalysisProvider`, `AnalyzeAsync(token)` | Optional explicit host diagnostics. Attached edits debounce requests; no provider returns explicit `Unavailable`. Invalid/provider-failed awaited operations throw and mark current results `Failed`; cancellation/stale results never publish diagnostics. |
| `EnableSyntaxHighlighting` | Default true; local light/dark palette. Disable for host-managed high contrast; never changes global resources. |
| `CompletionProvider`, `RequestCompletionAsync(token)` | Optional injected `IEditorCompletionProvider`; no implicit completion. Request contains full text/version/caret. Response contains matching version, validated replacement span and immutable items. Stale text/caret/lifetime/provider results throw cancellation; failures propagate. |
| `ApplyCompletion(list, index)` | Validates version/span/index/read-only and makes an undoable replacement. Use only results from this control. |
| `ShowCompletionAsync(token)`, `CloseCompletion()`, `IsCompletionOpen`, `CompletionPopup` | Shows/dismisses only injected completion; the popup property is an advanced presentation extension. Read-only hosts may explicitly show display-only suggestions; insertion still rejects read-only/protected text. |
| `CompletionAcceptsEnter` | Default true; false leaves Enter as normal editing while Tab can accept completion. |
| `EnableExecutionGestures`, `ExecutionRequested` | Opt-in, default false. Focus-local F5=document, F8=selection/current line, Ctrl+Pause=stop event. Run events contain snapshots; Stop has none. No invocation, engine fallback or global hooks. |
| `ErrorOccurred` | Automatic/keyboard failures are traced and surfaced with operation/original exception. Explicit awaited operations propagate instead. Cancellation is not failure. |
| `Dispose()` | UI-thread, idempotent, permanent; cancels work and releases document subscriptions/completion/search UI. Never disposes the document. |

Detachment cancels work, makes diagnostics unavailable, closes completion UI and disconnects the editing area from the host document. Reattachment resumes configured host analysis and restores clamped/normalized positions. Dispose permanently removed controls. Ignoring providers may keep their own tasks alive until they complete, but cannot publish stale results.

An open popup can filter continued user text input within its replacement span. This tracks only those input-path insertions against the current document version and anchored range; acceptance revalidates them. Host document mutations, unrelated edits, navigation, read-only changes and lifetime changes invalidate it. Returned immutable completion lists remain tied to their original version, so `ApplyCompletion` still rejects stale public results.

## Keyboard, accessibility and platform limits

AvaloniaEdit retains editing, navigation, selection, undo/redo, copy/paste and Ctrl+F find. Ctrl+Space opens only injected completion. The shared Iseberg automation peer exposes Edit/Value, focus, text changes and read-only state; it is not a complete UI Automation TextPattern implementation. Hosts must label their toolbar, report errors and choose high-contrast behavior.

Package-only headless Fluent/Skia checks assert an actual rendered frame and engine independence, including DSC/ordinary/error text, and preserve an explicitly host-owned unopened default runspace. The PowerShell reference is **test-only** in that consumer, for observing the host; it is not an editor producer or editor-only desktop example dependency. Windows native editor smoke exercises rendering, focused text input, synthetic undo/completion gestures, automation properties and detach/reattach without loading PowerShell at all. The separate runtime-owning Iseberg app has full workbench regression coverage, including debugging, console completion, prompts, persistence and remoting.

Physical keyboard hardware, real screen readers and native Linux/macOS interaction are not certified by these tests. Fonts, clipboard availability, native Avalonia prerequisites, secure prompt presentation, host parser/engine distribution and licenses remain the host distributor's responsibilities. Trim/NativeAOT publishing is not verified. See [distribution notices](editor-notices.md).
