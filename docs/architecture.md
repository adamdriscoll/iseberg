# Architecture

Iseberg is a cross-platform desktop workbench inspired by PowerShell ISE.

## Module responsibilities

| Module | Responsibility |
|---|---|
| Avalonia application and `MainWindow` | Desktop lifecycle, layouts, menus, toolbar actions, dialogs and keyboard routing |
| `WorkbenchModel` and `SessionModel` | Independent session presentation, file tabs, command history and output batching |
| `PowerShellSession` | Persistent runspace, serialized execution, asynchronous stopping, prompt refresh, completion and debugger events |
| `WorkbenchHost` | PowerShell input, credentials, choices, output streams, console clearing and progress |
| `ScriptFile` and `ScriptTab` | File metadata and persistence, dirty state, per-file text documents, undo histories and breakpoint anchors |
| `EditorAnalysis` and editor rendering | Parser-based syntax coloring, diagnostics, folding, completion context and matching-brace navigation |
| `ConsoleBuffer` | Protected transcript and prompt, editable input, output spans and buffer limits in one text document |
| `ReplacementSearch` and `ReplaceWindow` | Literal/regex replacement, search options, selection scope and undo grouping |
| `SnippetCatalog` and `SnippetWindow` | Built-in/custom snippets, XML import/export, recursive discovery, previews, caret positioning and indentation |
| `UserSettings` and Options | Persisted preferences, themes, fonts, colors, completion behavior and recovery settings |

## Design decisions

The engine is a deep module: callers supply code and consume output/state/input/debug events. Runspace ownership, pipeline serialization, host protocol and stopping are localized there; the desktop never launches a new shell for each command. No speculative host abstraction is introduced.

Script files retain their own text documents and undo histories. File persistence preserves recognized Unicode BOMs and line endings, marks a file saved only after a successful write, and replaces files through a temporary file in the destination directory.

PowerShell parser tokens drive highlighting and incomplete-input detection rather than a second regex language parser. PowerShell itself supplies completion and command metadata. UI output is drained in batches and the console buffer is bounded.

`ConsoleBuffer` localizes transcript protection, active input, prompt transitions, output spans and trimming in one document. The editor's read-only-section interface protects the transcript even when a selection spans output and input; whole-value automation writes cannot bypass it. Host changes reset console undo rather than allowing an undo operation to remove output or prompts.

Snippet parsing and serialization are engine-independent. Import validates the PowerShell namespace/schema and metadata with DTDs/external entity resolution disabled. Stored custom snippets are plain XML, loaded without execution. The built-in catalog includes workflow/classic DSC authoring entries but labels their Windows PowerShell requirements explicitly.

Native title bars and file dialogs follow the host operating system.
