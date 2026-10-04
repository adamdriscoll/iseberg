# ISE parity inventory

This inventory distinguishes working behavior from the full ISE surface. "Implemented" does not mean pixel-identical. The workbench uses vector icons, Avalonia controls, native OS window chrome, and installed fallback fonts.

## Implemented

| Area | Behavior |
|---|---|
| Workbench | Classic light menu/toolbar, white editor, `#012456` blue console, red errors, yellow warnings, Commands pane, file/session tabs, status/caret/encoding/zoom |
| Sessions | Independent persistent PowerShell 7 runspaces, busy-state guards, asynchronous execution and stop, prompt refresh |
| Files | New/open/save/save-as/save-all, dirty markers, close/exit save prompts, recent files, Unicode encoding/BOM and line-ending preservation |
| Editing | Independent per-document undo/redo, clipboard, line numbers, indentation, word wrap, find, interactive literal/regex replacement with case/whole-word/direction/wrap/selection options and one-step Replace All undo, go-to-line, parser-based matching-brace navigation/selection |
| Language | PowerShell parser-based syntax colors, parse diagnostics, brace folding, real runspace completion, context-filtered automatic triggers, cancellable completion timeout, Enter-selection preferences |
| Snippets | Searchable preview catalog of all 26 ISE built-in titles; custom creation from selection, recursive discovery, validated ISE XML import/export, metadata/caret/indent preservation, duplicate filtering; default-snippet preference leaves custom snippets available |
| Console editing | One scrollable/selectable document containing protected output, earlier commands and prompt plus editable current input; syntax coloring, undo protection, history/draft recall, multiline continuation, Tab/Shift+Tab completion, command recall from the transcript |
| Execution | F5 script execution, F8 selection/current line, graphical console, command history, incomplete-input continuation |
| Host | Formatted PowerShell output, host writes, errors/warnings/verbose/debug, progress, Read-Host, secure input, choices, credential prompts, Clear-Host |
| Debugging | Saved-script line breakpoints, breakpoint clearing, source-line highlight, continue, step into/over/out, stop |
| Command discovery | Real command/module filtering, command definitions, insertion, execution, full local help |
| Preferences | Screenshot-aligned two-tab Options dialog; RGB/hex token and stream colors, fonts/point sizes and fixed-width filter, live sample, built-in/custom themes, outlining, completion/Enter/timeout, duplicate-file warning, save-before-run prompt, local/online help, toolbar/snippets, recovery interval and recent-file count; Apply/OK/Cancel/defaults; persisted layout/zoom/wrap/command pane/profile preferences |
| Desktop presentation | Windows system colors and message-font metrics, DPI-independent Options geometry and system-text-size scaling, live high-contrast overrides, action/menu/tab/window icons, resource-backed English Options/menu/toolbar/status labels |
| Keyboard and accessibility | Menu accelerators, enabled/check states, focus-preserving clipboard actions, editor context menus, F10 menu focus (outside paused debugging), Ctrl+Tab document/dialog navigation, F6 pane navigation, focus cues and named controls, editor automation value/read-only providers |
| Recovery | Atomic periodic recovery copies for unsaved scripts, startup recovery as unsaved documents, normal-close/save cleanup, other live instances excluded; original files are never autosaved |
| Policy | Existing Windows policy respected; explicit process-only RemoteSigned action |
| Development | Pinned SDK dependencies, engine/desktop tests, Windows/Linux/macOS CI, debug-only Avalonia developer tools |

## Not yet reproduced

- Visual presentation can differ from PowerShell ISE. Options uses compact control templates, but native chrome, installed fonts, antialiasing, DPI and accessibility text scaling can change physical pixels. Full native UI Automation text-range/navigation behavior and exhaustive screen-reader/menu interaction parity remain unverified. English resource infrastructure is implemented; translated resource packs are not included.
- Full Show-Command parameter-set/forms experience and expandable parameter controls.
- Paused-debugger command evaluation, variable/watch/call-stack panes, conditional/command/variable breakpoints, breakpoint enable/disable, live breakpoint editing and break-all. Editor line breakpoints do follow text insertions/deletions.
- Dedicated remoting tabs, interactive `Enter-PSSession`/runspace push-pop, remote file editing, and remote debugger UI.
- `$psISE` compatibility, WPF add-on hosting, custom menu/add-on scripting, and the ISE extension object model.
- Session restoration, external-file change detection, encoding picker/legacy code pages, printing, theme file import/export, and persisted splitter/window sizes. Recovery copies restore script text, not complete sessions or debugger state.
- Multi-choice graphical prompts, nested prompts, full character-buffer semantics, interactive native terminal applications, and complete host color/ANSI handling.
- Platform-specific installers, app bundles/signing, file associations, update delivery, and exhaustive Linux/macOS native-desktop verification.

## Intentional differences

The application hosts PowerShell 7 rather than Windows PowerShell 5.1. Windows-only modules and APIs are unavailable on other systems unless their own dependencies support those systems. ISE profiles use an Iseberg-specific host profile, and profiles are initially opt-in. Scripts run with the user's privileges; the editor is not a sandbox. Machine/user execution policy is never changed automatically.

Workflow/classic DSC templates are marked for Windows PowerShell 5.1; including them for authoring does not make their legacy syntax executable in PowerShell 7. Custom snippet files use the ISE XML format, but the ISE scripting object model and snippet cmdlets are not emulated.

The PowerShell execution gate prevents overlapping user commands and completion/metadata queries within a tab. Completion returns only when the same document/caret is still current; it does not reuse stale results. Console output is capped to keep long sessions responsive.

The console uses a protected transcript and prompt within a single editor, not a separate input widget. Host-output/prompt changes reset console undo to prevent editing executed text; script documents retain their own undo histories. Replacement uses .NET regular expressions and bounded matching time.
