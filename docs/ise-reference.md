# ISE reference findings

## Reference inspected

ILSpy was used on the Windows installation's `powershell_ise.exe` and the GAC assemblies `Microsoft.PowerShell.GPowerShell.dll` and `Microsoft.PowerShell.Editor.dll` (assembly version `3.0.0.0`, CLR v4). These are reference material only and are not project dependencies or redistributed artifacts.

The inspection used assembly/type inventories, the public/internal member surfaces of `PowerShellTab` and `CustomCommands`, and focused inspection of invocation and default-option behavior. This is not an exhaustive reverse engineering of every ISE subsystem or a claim of visual equivalence.

## Architectural observations

| Original responsibility | Observed behavior | Iseberg replacement |
|---|---|---|
| Executable/bootstrap | Small launcher around the WPF application | Avalonia desktop application |
| `MainWindow`, `RunspaceTabControl`, `RunspaceControl` | PowerShell tabs contain editors and a console | Shared workbench switches between independent session models |
| `PowerShellTab` | Owns runspace, files, input history, prompt, completion, execution state, and debugger | `PowerShellSession` owns the engine; desktop `SessionModel` owns per-tab presentation state |
| `ISEFile`, `ISEEditor` | File metadata, dirty state, editor operations, breakpoint state | `ScriptFile` and one AvaloniaEdit text document per file |
| `Microsoft.PowerShell.Editor` | Extensive Visual Studio text/editor/intellisense/outlining machinery tied to WPF | AvaloniaEdit plus real PowerShell parser tokens, completion, and folding |
| Asynchronous invocation | Execution and stopping are coordinated with UI responsiveness and readiness state | Worker execution, one pipeline gate per runspace, explicit asynchronous stop |
| Script/selection/console execution | Separate commands, same persistent session | Full file dot-sourcing, selection/current-line execution, console command execution |
| Host interaction | Graphical prompts, output buffer, stream-specific colors, progress UI | Original PowerShell host with input/credential/choice dialogs, output batching and progress |
| Debugger | Script breakpoints, paused source adornments, resume actions | PowerShell 7 debugger events and continue/step/stop actions |
| `CustomCommands` | Central set of workbench actions and ISE shortcuts | Menu/toolbar routing and native key handling |
| Default options | Script pane top; adjustable font/zoom, line numbers, outlining and colors | Top/right/maximized layouts, zoom, parser coloring, line numbers and folding |
| Add-on tools | WPF add-on controls and ISE-specific scripting object model | Not reused; a future native extension design is needed |

## Design decisions

The engine is a deep module: callers supply code and consume output/state/input/debug events. Runspace ownership, pipeline serialization, host protocol and stopping are localized there; the desktop never launches a new shell for each command. No speculative host abstraction is introduced.

Script files retain their own text documents and undo histories. File persistence preserves recognized Unicode BOMs and line endings, marks a file saved only after a successful write, and replaces files through a temporary file in the destination directory.

PowerShell parser tokens drive highlighting and incomplete-input detection rather than a second regex language parser. PowerShell itself supplies completion and command metadata. UI output is drained in batches and the console buffer is bounded.

The old WPF/Visual Studio editor, Windows-only tool panes, launcher splash screen, obsolete Windows PowerShell assumptions, and proprietary assets are deliberately not ported. Native title bars and file dialogs remain platform-native rather than imitating Windows chrome on other operating systems.
