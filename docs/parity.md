# ISE parity inventory

This inventory distinguishes working behavior from the full ISE surface. "Implemented" does not mean pixel-identical. The workbench uses original vector icons, Avalonia controls, native OS window chrome, and installed fallback fonts.

## Implemented

| Area | Behavior |
|---|---|
| Workbench | Classic light menu/toolbar, white editor, `#012456` blue console, red errors, yellow warnings, Commands pane, file/session tabs, status/caret/encoding/zoom |
| Sessions | Independent persistent PowerShell 7 runspaces, busy-state guards, asynchronous execution and stop, prompt refresh |
| Files | New/open/save/save-as/save-all, dirty markers, close/exit save prompts, recent files, Unicode encoding/BOM and line-ending preservation |
| Editing | Independent per-document undo/redo, clipboard, line numbers, indentation, word wrap, find, literal case-insensitive replace, go-to-line |
| Language | PowerShell parser-based syntax colors, parse diagnostics, brace folding, real runspace completion, three original structure snippets |
| Execution | F5 script execution, F8 selection/current line, graphical console, command history, incomplete-input continuation |
| Host | Formatted PowerShell output, host writes, errors/warnings/verbose/debug, progress, Read-Host, secure input, choices, credential prompts, Clear-Host |
| Debugging | Saved-script line breakpoints, breakpoint clearing, source-line highlight, continue, step into/over/out, stop |
| Command discovery | Real command/module filtering, command definitions, insertion, execution, full local help |
| Preferences | Layout, zoom, line numbers, wrap, command pane visibility, profile opt-in, recent files persisted to local application data |
| Policy | Existing Windows policy respected; explicit process-only RemoteSigned action |
| Development | Pinned SDK dependencies, engine/desktop tests, Windows/Linux/macOS CI, debug-only Avalonia developer tools |

## Not yet reproduced

- Exact Windows theme metrics, all icons, high-contrast/accessibility parity, translated resources, and all focus/menu interactions.
- ISE's single editable console buffer, auto-completion heuristics, rich replacement options, matching-brace navigation, and the complete built-in/custom snippet catalog.
- Full Show-Command parameter-set/forms experience and expandable parameter controls.
- Paused-debugger command evaluation, variable/watch/call-stack panes, conditional/command/variable breakpoints, breakpoint enable/disable, live breakpoint editing and break-all. Editor line breakpoints do follow text insertions/deletions.
- Dedicated remoting tabs, interactive `Enter-PSSession`/runspace push-pop, remote file editing, and remote debugger UI.
- `$psISE` compatibility, WPF add-on hosting, custom menu/add-on scripting, and the ISE extension object model.
- Autosave/crash recovery, session restoration, external-file change detection, encoding picker/legacy code pages, printing, comprehensive color/theme/font options, and persisted splitter/window sizes.
- Multi-choice graphical prompts, nested prompts, full character-buffer semantics, interactive native terminal applications, and complete host color/ANSI handling.
- Platform-specific installers, app bundles/signing, file associations, update delivery, and exhaustive Linux/macOS native-desktop verification.

## Intentional differences

The application hosts PowerShell 7 rather than Windows PowerShell 5.1. Windows-only modules and APIs are unavailable on other systems unless their own dependencies support those systems. ISE profiles use an Iseberg-specific host profile, and profiles are initially opt-in. Scripts run with the user's privileges; the editor is not a sandbox. Machine/user execution policy is never changed automatically.

The PowerShell execution gate prevents overlapping user commands and completion/metadata queries within a tab. Completion returns only when the same document/caret is still current; it does not reuse stale results. Console output is capped to keep long sessions responsive.
