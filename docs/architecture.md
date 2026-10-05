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
| `CommandForm` and `CommandFormView` | Parameter-set metadata, per-session drafts, literal/expression serialization, required-field validation, expandable typed editors and a shared preview for pane/dialog actions |
| `CommandHelpDocument`, `CommandHelpWindow` and `HelpSettingsWindow` | Structured PowerShell help sections, selectable/read-only help presentation, literal search and zoom, isolated settings drafts and persistent help preferences |
| `UserSettings` and Options | Persisted preferences, themes, fonts, colors, completion behavior and recovery settings |

## Design decisions

The engine is a deep module: callers supply code and consume output/state/input/debug events. Runspace ownership, pipeline serialization, host protocol and stopping are localized there; the desktop never launches a new shell for each command. No speculative host abstraction is introduced.

Script files retain their own text documents and undo histories. File persistence preserves recognized Unicode BOMs and line endings, marks a file saved only after a successful write, and replaces files through a temporary file in the destination directory.

PowerShell parser tokens drive highlighting and incomplete-input detection rather than a second regex language parser. PowerShell itself supplies completion and command metadata. UI output is drained in batches and the console buffer is bounded.

Command metadata is queried through the same runspace gate as completion, with cancellable, serialized requests. Alias targets supply parameter metadata while invocation retains the selected alias or module-qualified command. Forms retain values across parameter sets and sessions but serialize only the active set. Run, Insert and Copy share one generated command; literal text is quoted, and expression evaluation requires an explicit per-parameter choice. Late responses from a previous command or tab cannot replace the current form.

`CommandFormView` shares editing/validation logic between the pane's expandable controls and the standalone dialog's compact parameter-set tabs and label/value rows. Common parameters remain in a separate expander. Compact rows expose advanced inclusion/expression controls through their context menus, keeping the default form close to ISE without dropping expression or explicit-value support.

Help rendering uses PowerShell's structured properties for the ten selectable sections; it does not split formatted output on language-dependent headings. `HelpViewSettings` stores section selection, case/whole-word search and zoom, and is deep-copied with the rest of `UserSettings`. Help settings use a private draft, publish only after OK and a successful save, and preserve the previous document/preferences on save failure. The help window reuses `ReplacementSearch` for bounded literal search and the accessible editor for selectable, read-only content.

The console's `Show-Command` function delegates to a host-aware cmdlet and retains precedence when PowerShell auto-imports `Microsoft.PowerShell.Utility`. The native command otherwise tries to load WPF types unavailable to the Avalonia host. The bridge resolves metadata and help through nested invocations on the current pipeline thread, then awaits a desktop `ShowCommandRequest`. The desktop returns generated text; the cmdlet either returns it for `-PassThru` or invokes it in the current pipeline, preserving structured output and access to session variables. It does not call `ExecuteAsync` or metadata queries from the UI while holding the runspace gate. Stop cancels the request and closes the form or command picker.

`ConsoleBuffer` localizes transcript protection, active input, prompt transitions, output spans and trimming in one document. The editor's read-only-section interface protects the transcript even when a selection spans output and input; whole-value automation writes cannot bypass it. Host changes reset console undo rather than allowing an undo operation to remove output or prompts.

Snippet parsing and serialization are engine-independent. Import validates the PowerShell namespace/schema and metadata with DTDs/external entity resolution disabled. Stored custom snippets are plain XML, loaded without execution. The built-in catalog includes workflow/classic DSC authoring entries but labels their Windows PowerShell requirements explicitly.

Native title bars and file dialogs follow the host operating system.
