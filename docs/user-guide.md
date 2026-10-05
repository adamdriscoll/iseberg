# User guide

[Back to README](../README.md)

## Contents

- [Workbench and keyboard shortcuts](#workbench-and-keyboard-shortcuts)
- [Console and PowerShell tabs](#console-and-powershell-tabs)
- [Editing scripts](#editing-scripts)
- [Snippets](#snippets)
- [Execution and profiles](#execution-and-profiles)
- [Host prompts, colors, and native terminals](#host-prompts-colors-and-native-terminals)
- [Remoting](#remoting)
- [Debugging](#debugging)
- [Files, sessions, and printing](#files-sessions-and-printing)
- [Options and accessibility](#options-and-accessibility)

## Workbench and keyboard shortcuts

The default view has a white script editor above the blue console, a Commands pane on the right, and the familiar menu, toolbar, file tabs, and status/zoom bar. The PowerShell session tab row appears when more than one session is open or a remote connection is active, so a single local session's script tabs sit directly beneath the toolbar.

| Action | Shortcut |
|---|---|
| New / open / save script | Ctrl+N / Ctrl+O / Ctrl+S |
| Save as / close script | Ctrl+Shift+S / Ctrl+W |
| Print script snapshot | Ctrl+P |
| New / close PowerShell tab | Ctrl+T / Ctrl+Shift+W |
| Run script / continue debugging | F5 |
| Run selection, or current line | F8 |
| Stop execution | Ctrl+Break or Shift+F5 |
| Toggle breakpoint | F9 |
| Remove all breakpoints | Ctrl+Shift+F9 |
| Break running script | Ctrl+Alt+Break |
| Step over / into / out | F10 / F11 / Shift+F11 |
| Completion / snippets | Ctrl+Space / Ctrl+J |
| Find / replace / go to line | Ctrl+F / Ctrl+H / Ctrl+G |
| Matching brace / select to matching brace | Ctrl+] / Ctrl+Shift+] |
| Script top / right / maximized | Ctrl+1 / Ctrl+2 / Ctrl+3 |
| Focus script / console | Ctrl+I / Ctrl+D |
| Clear console | Ctrl+L |

On macOS, Command is also accepted for the workbench shortcuts.

## Console and PowerShell tabs

Console Up/Down retrieves command history; Shift+Enter inserts a newline. Enter leaves syntactically incomplete commands open for more input. Each PowerShell tab has an independent persistent runspace, so variables, functions, current directory, and loaded modules survive successive commands without leaking into another tab.

The console is one text buffer and one scrollable editor: output, earlier commands, the prompt, and current input can be selected and copied together. Only the current input is editable; transcript text and prompt characters are protected from typing, deletion, paste, and automation writes. Enter on an earlier command recalls its code into the current input without executing it. Escape dismisses completion first, or clears input when no completion is open. Output/prompt updates reset console undo so undo cannot modify the transcript; script undo histories remain independent.

`Clear-Host` and its `clear`/`cls` aliases clear the graphical console on every supported platform without invoking a native terminal program or resetting the PowerShell session.

## Editing scripts

### IntelliSense

IntelliSense uses PowerShell completion in the active runspace. Automatic triggers include variables (`$`), command/parameter names (`-`), members (`.` / `::`), types (`[`), provider paths (`\` / `/`), and values after a parameter name and space.

A new trigger refreshes an open list for the current context, such as switching from commands to parameters in `Get-Process -`. Parser context suppresses comments, literal strings, numeric decimal points, and arithmetic operators; expandable-string variables and subexpressions are supported.

Ctrl+Space requests completion explicitly, even with automatic IntelliSense disabled. Console Tab / Shift+Tab cycles results when the completion list is closed. The timeout preference limits a completion query, rather than delaying its appearance.

### Find, replace, and matching braces

**Replace** provides literal or .NET regular-expression matching, case and whole-word options, upward search, wrap-around, and a selection-only scope. Regex replacements support capture groups such as `$1` and `${name}`; literal replacements do not interpret dollar signs. Find Next, Replace Next, and Replace All stay available in one dialog, with explicit match/error feedback. Replace All is one undoable edit. Matching-brace navigation uses parser tokens, ignores braces in comments/literal text, includes nested subexpressions, and expands script folds when needed.

## Snippets

### Browse and insert

Ctrl+J opens a searchable catalog with descriptions, author information, and code previews. The catalog covers all 26 ISE built-in titles: conditionals, loops, functions/advanced functions, switch, error handling, comments, classes, workflows, and DSC structures. Insertion respects document indentation and line endings, positions the caret from snippet metadata, and is undoable.

### Create, import, and export

**Tools > Create Snippet** saves selected script text as a custom snippet. **Import Snippets** reads standard ISE `.snippets.ps1xml` files, including title, description, author, `CaretOffset`, and `Indent`; **Export Snippets** writes the custom catalog in that format. Imports are validated before persistence, duplicate custom entries are removed, and malformed files are reported instead of silently skipped.

Custom files are discovered recursively in `Iseberg/Snippets` under local application data. On Windows, the catalog also reads the user's `Documents/WindowsPowerShell/Snippets` folder without modifying those files. Disabling **Use default snippets** hides only built-ins, not custom snippets.

### Legacy templates

Workflow and classic DSC entries are explicitly labeled as Windows PowerShell 5.1 templates. They are available for authoring legacy scripts, but this PowerShell 7 host does not add support for executing legacy workflow/configuration syntax. The catalog does not implement `$psISE` or the ISE snippet-management cmdlets.

## Execution and profiles

### Execution policy

**Windows execution policy is respected.** If local `.ps1` files are blocked, use **Tools > Enable Local Scripts (Process Only)** and explicitly approve `RemoteSigned`. This changes only the application process, affects all its runspaces, and does not override Group Policy or change user/machine settings.

### Saved and unsaved scripts

Running named files uses their real paths, preserving `$PSScriptRoot`, `$PSCommandPath`, and debugger source locations.

The save-before-run preference prompts for modified scripts; explicitly choosing **Run without saving** executes the edited text in memory without named-file metadata and is not allowed with breakpoints. With prompting disabled, modified named scripts are saved automatically; untitled scripts run in memory unless breakpoints require saving.

### Profiles

Profiles are opt-in under **Tools > Load profiles in new PowerShell tabs**. `$PROFILE` points to `Iseberg_profile.ps1` in the usual user PowerShell configuration directory, alongside the shared `profile.ps1`. Loading profiles executes all four profile locations in normal PowerShell order. Startup never silently changes execution policy or executes user profiles.

### Permissions and host behavior

Scripts execute with your account's permissions. This is **not a sandbox**. The console is a graphical PowerShell host with a protected transcript, not a terminal emulator. Interactive native applications run in a [separate system terminal](#interactive-native-applications).

## Host prompts, colors, and native terminals

### Input dialogs

`Read-Host`, secure input, credentials, and field prompts use graphical dialogs. Single-choice prompts use radio buttons; multiple-choice prompts use checkboxes with the supplied defaults and help text. Submit returns the selected indices, including an explicitly empty multiple selection. Cancel, Escape, or closing an input dialog stops the requesting pipeline. Stop also cancels outstanding input and closes its dialog; secure values are not written to the transcript.

### Nested prompts

Local scripts can call `$Host.EnterNestedPrompt()`. The console changes to `[Nested 1]: PS> ` and accepts commands and completion in the suspended scope, including function-local variables. Further nested prompts increase the depth. Enter `exit` or call `$Host.ExitNestedPrompt()` to return one level; Stop cancels the whole execution rather than resuming the suspended script.

The normal execution gate remains occupied, so another script cannot run concurrently. Nested host prompts inside debugger evaluation are rejected explicitly: the debugger already provides its own suspended-scope console. Remote nested prompts are unsupported; use remote breakpoints and the debugger console instead.

### Output colors

`Write-Host -ForegroundColor/-BackgroundColor`, colored `$Host.UI.Write(...)`, and changes to `$Host.UI.RawUI.ForegroundColor/BackgroundColor` affect subsequent output without recoloring earlier transcript text. ANSI SGR sequences support normal/bright 16-color palettes, 256-color palettes, RGB foreground/background, bold, underline, reverse video, and their resets. Styling can span output writes; copied text contains no styling escapes. Theme colors provide defaults, and high contrast overrides custom output styling. `Clear-Host` resets ANSI parser state. An incomplete escape at the end of output is discarded with a warning.

### Terminal limitations

The transcript does **not** implement cursor movement, erasure, screen switching, OSC title/hyperlink commands, raw key events, or character-cell reads/writes/scrolling. Unsupported ANSI controls are discarded with one warning per cleared transcript, never applied to earlier commands or current input. Backspace and carriage-return output cannot overwrite earlier text.

Raw cursor/window positioning, resizing, cursor-size changes, `ReadKey`, `FlushInputBuffer`, and character-buffer operations raise explicit unsupported-host errors. `KeyAvailable` is always false; buffer/window sizes are fixed formatting hints, not a real screen. `WindowTitle` is stored host metadata only. The whole-buffer clear sentinel remains supported for `Clear-Host`.

### Interactive native applications

For an interactive native application, explicitly opt into a separate terminal:

```powershell
Start-IsebergTerminal my-native-app -ArgumentList @('argument with spaces') -WorkingDirectory $PWD
```

The application must be an installed native executable discoverable by PowerShell, or an executable path.

| Platform | Terminal requirement |
|---|---|
| Windows | Opens a new native console |
| Linux | Requires **xterm and a graphical display** |
| macOS | Uses **Terminal.app** |

Missing applications, launch failures, terminal closure without an exit status, and unsupported environments are reported as errors. Arguments are passed as literal values, not interpolated shell code. Iseberg waits for completion, returns the integer exit code, and sets `$LASTEXITCODE`. Stop terminates the launched application's process tree, not unrelated terminals.

Terminal input/output stays in that terminal, not in the protected transcript or PowerShell's object pipeline. The native process inherits the environment and selected filesystem directory but cannot share in-memory runspace variables or functions. Launching another PowerShell this way requires an installed `pwsh`; the embedded SDK alone is not a `pwsh` executable.

Ordinary noninteractive native commands can still stream text through the graphical console; direct native execution warns that interactive input is unavailable and must use the terminal command. Terminal launch is local-only. Windows terminal input/output and stopping have been exercised locally; native Linux/macOS terminal interaction still requires platform-specific verification.

### Show-Command error popups

The hosted `Show-Command -ErrorPopup` option displays execution errors in a read-only graphical popup instead of the normal error stream. Successful results remain structured PowerShell objects evaluated in the originating scope. Closing the popup dismisses it; Stop cancels it and the active execution. `-PassThru` returns the generated command without executing it, so no execution-error popup is shown.

## Remoting

### Connect and disconnect

**File > New Remote PowerShell Tab** opens a dedicated tab using SSH or WSMan.

- **SSH** accepts a hostname, optional user/key, port, and PowerShell subsystem; configure the server's SSH subsystem first and use local OpenSSH configuration/key authentication.
- **WSMan** accepts an `http`/`https` endpoint URI (including its port and `/wsman` path) and optional credentials, using default authentication. WSMan requires platform support, normally Windows.

The workbench does not provision servers, bypass host/certificate verification, or save credentials.

You can also run `Enter-PSSession` in any local tab, including `Enter-PSSession -Session $session` with an existing session. The host pushes that runspace; subsequent console commands, completion, command forms, and debugging use the remote connection. Tab captions and console/debugger prompts identify the remote machine.

Enter `Exit-PSSession` (or `exit`) as a standalone console command, or choose **File > Exit Remote Session**, to restore the original local variables, functions, directory, and breakpoint configuration. Nested interactive sessions are rejected explicitly. Connection loss is reported in the console and restores the local runspace.

Borrowed `PSSession` runspaces remain owned by PowerShell; temporary `Enter-PSSession` connections and connections created by the remote-tab dialog are closed when exited or the tab closes.

### Remote files and execution

**File > Open Remote File** opens a filesystem path on the active remote machine. Remote documents have a machine label, independent undo/dirty state, and retain their Unicode encoding/BOM and line endings. Save and Save As write through that connection, using an atomic replacement on the server; Save As confirms before replacing another file. Saving an untitled script in a remote tab asks for a remote path. Remote paths never enter local recent-file history. Recovery copies remain local plaintext and reopen as detached, unsaved documents rather than reconnecting automatically.

F5 executes saved remote scripts at their remote paths, preserving source locations and `$PSScriptRoot`. Local files stay local: use F8 to execute their text remotely, or open/save a remote copy for named-file execution and breakpoints. A document cannot be saved or run through a different connection, even to the same machine; reopen it after reconnecting.

### Remote debugging and command forms

Remote breakpoint stops open remote source in the editor and show the same Variables, Watch, Call Stack, and Breakpoints panes, with console evaluation, completion, stepping, continue, and stop. Remote variable inspection/evaluation uses only the stopped frame; callers remain navigable but cannot be inspected.

Expanded objects are remoting-serialized snapshots, subject to PowerShell's serialization depth, not live local objects. Remote breakpoint specifications are kept separate from local ones and are not persisted as the local tab's debugger configuration. Desktop Show Command uses remote metadata; the console's Avalonia `Show-Command` bridge is local-only.

## Debugging

### Breakpoint markers

F9 or a left-click in the dedicated gutter beside a script line toggles its breakpoint; the marker follows inserted/deleted text. The gutter stays separate from the text, even with line numbers hidden or the editor scrolled/wrapped. Enabled breakpoints use filled red circles, disabled breakpoints use hollow circles, and the stopped statement has an arrow. Subtle translucent line highlights preserve the editor's background and syntax colors in light/dark themes; high contrast relies on gutter symbols without tinting the text.

### Variables, watches, and call stack

**Debug > Debugger Panes** shows Variables, Watch, Call Stack, and Breakpoints in a full-height, resizable dock to the right of the editor/console, and opens automatically when execution pauses. The debugger and Commands docks can be shown or closed independently; their widths persist across visibility changes and application restarts.

Expand variable/watch objects to inspect properties, dictionary entries, and indexed array/list elements. Children load on demand in pages of up to 100, with **Load more** for larger collections. Property-getter failures appear beside the affected member. Refreshing or resuming discards old object references.

Add PowerShell expressions in Watch; results refresh on each pause, after console evaluation, or with **Refresh**. Evaluation failures appear beside the affected watch. Select a call-stack frame to inspect its local variables; double-click to navigate to its source.

The pane labels the inspected frame and explicitly identifies evaluation as using the stopped frame. Commands, F8, watches, and completion always use the stopped scope, not the selected caller. PowerShell does not expose arbitrary caller-scope execution; callers whose variable scope cannot be verified, including some dotted/module boundaries, report an inspection error instead of showing another scope's variables.

### Evaluate, step, continue, and stop

While paused, enter commands in the console at the `[DBG]: PS>` prompt, or use F8 to evaluate a selection/current line without resuming the script. Assignments modify the suspended scope. F5 continues; F10/F11/Shift+F11 step over/into/out. **Debug > Break All** (Ctrl+Alt+Break) requests a pause at the next PowerShell statement; it does not interrupt an in-progress native command or cmdlet. Stop still cancels execution and any active debugger evaluation.

### Create and manage breakpoints

**Debug > New Breakpoint** creates line, command, or variable breakpoints.

- **Line breakpoints** need a saved script path.
- **Command and variable breakpoints** can optionally be restricted to a script. Variable breakpoints support Read, Write, or ReadWrite.
- **Conditions** are PowerShell expressions that pause only when true.
- **Actions** are arbitrary PowerShell code; include `break` to pause.

The Breakpoints pane supports editing, enabling/disabling, and deletion without restarting execution. Edits requested while a script is running first wait for a pause at the next PowerShell statement, then apply the change and leave execution paused. The status bar explains the wait; an in-progress cmdlet/native command must return before the pause can occur.

F9 retains the originally requested source line even if the pause navigates elsewhere. Disabled line markers are hollow. Ctrl+Shift+F9 removes every breakpoint in the active PowerShell tab, including command and variable breakpoints.

### IntelliSense while paused

Paused IntelliSense uses PowerShell's debugger command queue, so variables and members are completed against the suspended runspace. Console Tab/Shift+Tab and Ctrl+Space remain available. Ctrl+Space can also display suggestions in the paused script editor, but accepting a suggestion cannot modify that read-only script. Completion cancellation/timeouts do not resume the script.

### Persistence and side effects

Watches, conditions, actions, property getters, and console commands execute real PowerShell with your permissions and can have side effects.

Watches and breakpoint specifications (including conditions/actions, enabled state, and variable access modes) persist independently by PowerShell tab name in the user's `Iseberg/settings.json`. Restored tabs and newly created numbered tabs load their matching configurations. This never restores running or suspended execution. Unsaved-script breakpoint markers, engine IDs/hit counts, selected frames, and inspected object values are not persisted. Saved watch expressions and breakpoint code are plaintext; avoid embedding secrets in them.

## Files, sessions, and printing

### Startup restoration

Startup automatically restores PowerShell tab names/order, local file tabs, selected tabs, caret positions, file encodings, and debugger-pane visibility from `Iseberg/workbench.json` under local application data. Each tab starts a **fresh local runspace**: variables, functions, modules, console history/output, suspended execution, engine IDs/hit counts, and object snapshots are not restored. The existing profile opt-in still applies. Remote connections and credentials are never restored; clean remote documents are not reopened automatically. A workbench owned by another running instance is not restored.

Workbench metadata is checkpointed every five seconds and when opening files or performing menu actions; normal exit captures it before disposing the tabs. It contains paths and configuration, not script text. Named local documents are read from their current disk versions. Untitled tabs reopen as blank placeholders until recovery is accepted. Missing/unreadable documents are reported rather than silently recreated or written to disk.

### Unsaved recovery

Recovery stays separate:

- **Recover** opens interrupted unsaved text as detached, unsaved copies in the matching PowerShell tab, replacing an untitled placeholder when associated. A recovered named file does not replace its original disk-backed tab or overwrite its original path. Recovery includes the selected encoding.
- **Discard** deletes those recovery copies.
- **Cancel** leaves them for a later startup.

Normal save/discard/closure removes a document's recovery copy.

### External changes and save conflicts

Local files and files in an idle owning remote connection are checked for external byte changes on activation and every five seconds while the workbench is active. **Reload** explicitly discards this tab's edits and undo history; **Keep Edits** preserves them without authorizing a future overwrite. Every local or remote save checks the destination's content version, including deletion and changes that retain the same timestamp/size. A conflict offers reload, cancel, or explicit **Overwrite**; another change after that decision rejects the write. Save As also confirms an existing destination. Atomic replacement and strict encoding failures leave unsaved edits intact.

### File encoding

**File > File Encoding** selects UTF-8 with/without BOM, UTF-16/UTF-32 byte order and BOM variants, or a legacy code page.

- **Convert on save** changes the next write and marks the document modified; characters unsupported by the selected encoding are rejected, never replaced by question marks.
- **Reload with encoding** reinterprets the on-disk bytes and confirms before discarding edits.

Unicode BOMs and existing line endings are preserved by default. Invalid BOM-less UTF-8 prompts for an explicit encoding rather than guessing a legacy code page.

### Printing

**File > Print** (Ctrl+P) opens an offline HTML snapshot of the current script, including unsaved edits, in the default browser. Use the preview's Print button or the browser's print command to choose a printer or Save as PDF. Script markup is escaped and no external resources are loaded. The preview is a local plaintext temporary file, owner-only on Unix, and is removed on normal workbench closure; printing does not save the script or change its dirty state.

## Options and accessibility

### Preferences

**Tools > Options** opens the two-tab, classic Windows-style dialog. **Colors and Fonts** provides script/console token and output-stream colors, RGB/hexadecimal editing, installed-font selection, a fixed-width font filter, point sizes, a live sample, and named themes. The default is Lucida Console at 9 points, with installed monospace fallbacks on other platforms. **General Settings** controls outlining, line numbers, duplicate-file warnings, save-before-run prompts, pane position, automatic IntelliSense and Enter selection, completion timeout, local/online help, the toolbar, built-in snippets, recovery interval, and recent-file count.

### Import and export themes

**Manage Themes > Import / Export** transfers a versioned Iseberg JSON theme containing every editor/console/stream color and the font family/point size. Export writes the current Colors and Fonts draft. Import validates the entire file before changing the draft, confirms replacement of a same-named custom theme, and cannot replace built-in theme names. Malformed or unsupported files are reported, not converted into defaults. Imported changes reach the workbench/settings only after Apply or OK.

### Apply or discard settings

- **Apply** persists both tabs without closing the dialog.
- **OK** applies and closes.
- **Cancel** discards only changes made since the last Apply.
- **Restore Defaults** resets the dialog's preferences but preserves saved custom themes, recent-file history, zoom, Commands pane visibility, word wrap, profile opt-in, and window/pane geometry.

Setting the recovery interval or recent-file count to `0` disables that feature.

### Window and pane layout

Window size, position, maximized state, separate top/right script-to-console ratios, and debugger/Commands widths persist on normal exit. Restored windows are constrained to an available monitor's work area; unavailable monitor positions move to the primary display. Minimizing does not replace the saved normal size.

### Autosave

Autosave writes **recovery copies**, never overwrites a script on disk, and preserves its unsaved status. The recovery folder is created only when a dirty script is autosaved; cleanup does not require it to exist. After an interrupted session, startup offers to recover those copies as unsaved documents. Saving, discarding, or closing a script normally removes its copy.

Recovery files are plaintext in `Iseberg/Recovery` under local application data; Unix copies are owner-only. Files belonging to another running instance are not recovered.

### Accessibility and keyboard navigation

Windows system colors, message-font metrics, and high-contrast changes are reflected in the interface; high contrast overrides custom editor colors without modifying saved themes. Controls expose accessible names and keyboard focus cues, and editors expose their text and read-only state through an automation value provider. Native screen-reader text-navigation parity has not been verified. UI labels for Options, menus, toolbar actions, and status are backed by English `.resx` resources, with English fallback for untranslated cultures.

Use **Ctrl+Tab / Ctrl+Shift+Tab** to switch script tabs, **Ctrl+Tab** to switch Options pages, **F6 / Shift+F6** to cycle visible workbench panes, and **F10** to focus the menu when not paused in the debugger. Menus retain the last text-editing target for clipboard actions; editors also have context menus.
