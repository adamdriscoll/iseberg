# ISE scripting compatibility

Iseberg provides a **subset** of the Windows PowerShell ISE scripting object model in its local PowerShell 7 runspaces. It does not load `Microsoft.PowerShell.Host.ISE`, emulate Windows PowerShell 5.1, or host WPF controls. The objects below are Iseberg types with familiar member names, not binary-compatible ISE classes.

`$psISE` is available before opt-in profiles run. Put initialization scripts in `$PROFILE.CurrentUserCurrentHost` (`Iseberg_profile.ps1`), then explicitly enable profile loading or use **Tools > Load PowerShell Profiles**. Startup does not automatically execute add-ons or import the legacy `ISE` module.

## Supported members

Collections support zero-based indexing, `Count`, and enumeration. File, tab, and menu objects retain identity while open; methods reject objects belonging to another tab/window or removed objects. `CurrentPowerShellTab` and `CurrentFile` follow the **selected UI tab/file**, which can differ from the runspace executing a script.

| Object | Supported members |
|---|---|
| `$psISE` | Read-only `CurrentPowerShellTab`, `CurrentFile`, `CurrentEditor`, `PowerShellTabs` |
| `PowerShellTabs` | Indexing, enumeration, `Count`, `SetSelectedPowerShellTab(tab)` |
| PowerShell tab | Read-only `DisplayName`, `Prompt`, `CanInvoke`, `Files`, `AddOnsMenu`, `Snippets` |
| `Files` | `Add()`, `Add(fullPath)`, `Remove(file)`, `Remove(file, force)`, `SetSelectedFile(file)` |
| File | Read-only `DisplayName`, `FullPath`, `IsUntitled`, `IsSaved`, `Encoding`, `Editor`; `Save()`, `Save(encoding)`, `SaveAs(fullPath)`, `SaveAs(fullPath, encoding)` |
| Script editor | Read/write `Text`; read-only `LineCount`, `CaretLine`, `CaretColumn`, `CaretLineText`, `SelectedText`; `Clear()`, `InsertText(text)`, `GetLineLength(line)`, `SetCaretPosition(line, column)`, `Select(startLine, startColumn, endLine, endColumn)`, `SelectCaretLine()`, `EnsureVisible(line)`, `Focus()` |
| `AddOnsMenu.Submenus` and nested menu `Submenus` | `Add(displayName, action, shortcut)`, `Clear()`, `Remove(item)`, indexing, enumeration, `Count` |
| Menu item | Read-only `DisplayName`, `Submenus` |
| `Snippets` | Indexing, enumeration, `Count`, `Load(fullyQualifiedSnippetFilePath)`; entries expose `DisplayTitle`, `Description`, `Author`, `Text`, `CaretOffset`, `Indent`, `IsBuiltIn`, `Compatibility` |

`CurrentFile`/`CurrentEditor` can be null when no file is selected. `FullPath` is null for an untitled file. `IsSaved` means no unsaved content or encoding changes; use `IsUntitled` to determine whether a path exists. Tab display names are read-only: the numbered names remain the stable persistence/debugger keys.

Line and column numbers are **one-based**. The column immediately after a line's last character is valid; invalid positions and reversed selections raise errors. Positioning, selection, insertion, visibility, and focus methods select the owning file/tab. Text reads/writes and line-length queries do not require it to be visible. An inactive file has no active selection, so `SelectedText` is empty; its caret uses the stored position. Edits use the real AvaloniaEdit document, preserving dirty state, undo, and breakpoint anchors. Scripts cannot edit a paused debugger's read-only file or a remote document.

`Files.Add(fullPath)` opens a fully qualified **local** path, or selects an already-open local file. Relative paths are rejected. Save methods also require fully qualified local paths, use strict encodings and the existing atomic/version-checked save implementation, and do not display an overwrite dialog. `Save()` retains the current encoding; an explicit `System.Text.Encoding` selects its code page and BOM behavior. External changes and existing Save As destinations raise conflicts instead of being overwritten. Save methods are synchronous PowerShell APIs, not UI-thread APIs.

`Remove(file)` refuses dirty files; `Remove(file, $true)` explicitly discards edits. Removal is unavailable while debugging, in nested prompts, during autosave, when the document has breakpoint anchors, or for named files while a command is running. Stop execution and use the UI in those cases so engine breakpoints are cleaned up without reentering the running pipeline. Removing the last file leaves the collection empty until another is created. `CanInvoke` indicates idle local execution readiness; it does not promise support for ISE cross-tab `Invoke`.

### Edit a file

Run this in Iseberg's local console:

```powershell
Set-StrictMode -Version Latest
$file = $psISE.CurrentPowerShellTab.Files.Add()
$file.Editor.Text = "FIRST`nSECOND"
$file.Editor.Select(2, 1, 2, 7)
$file.Editor.InsertText($file.Editor.SelectedText.ToLowerInvariant())
$file.Editor.SetCaretPosition(1, 1)
$file.Editor.Focus()
# Use a new, fully qualified path:
$file.SaveAs((Join-Path $HOME 'iseberg-example.ps1'))
```

## Script-based Add-ons menus

Register actions from the **selected, owning local PowerShell tab**:

```powershell
Set-StrictMode -Version Latest
$tools = $psISE.CurrentPowerShellTab.AddOnsMenu.Submenus
$parent = $tools.Add('_My tools', $null, $null)
$item = $parent.Submenus.Add('_Insert timestamp', {
    $psISE.CurrentFile.Editor.InsertText((Get-Date).ToString('o'))
}, 'Alt+P')

# Later, remove this registration, or clear all custom items:
$parent.Submenus.Remove($item)
$tools.Clear()
```

Use `_` for access-key labels. A null action creates a submenu container; containers cannot have shortcuts, and action items cannot have submenus. Pass null/empty for no shortcut. Shortcuts use Avalonia key-gesture strings such as `Alt+P` or `Ctrl+Alt+K`, not WPF `KeyGesture` objects. Malformed gestures, duplicate sibling names, duplicate tab shortcuts, and workbench-reserved shortcuts are rejected.

Each tab has its own custom menu tree. Switching tabs displays only that tab's registrations; built-in Add-ons items remain intact when custom items are cleared. Shortcuts and clicks execute the **original ScriptBlock** in its owning local runspace, preserving variables, functions, module state, and `GetNewClosure()` bindings. Execution uses the usual serialization, output, debugger, and Stop paths. Actions are disabled while busy or connected to a pushed remote runspace; overlapping execution is rejected instead of queued.

Registrations and imported snippets are in-memory session state, not workbench checkpoints. Re-register them through the opt-in profile on restart. They execute with your account's permissions and are **not sandboxed**.

## Snippet-management cmdlets

These host cmdlets are available without importing the Windows PowerShell `ISE` module:

| Command | Supported behavior |
|---|---|
| `New-IseSnippet -Title <string> -Description <string> -Text <string> [-Author <string>] [-CaretOffset <int>] [-Force]` | Writes `<Title>.snippets.ps1xml` in Iseberg's user snippets directory. Title must be a valid filename without separators; caret is a zero-based offset from 0 through text length, default 0. Existing files require `-Force`. Also supports `-WhatIf`/`-Confirm`. No success output. |
| `Get-IseSnippet` | Returns `System.IO.FileInfo` objects for saved custom snippet files, not snippet text, built-ins, or session-only imports. Includes the legacy Windows user snippet directory when present. |
| `Import-IseSnippet -Path <fileOrDirectory> [-Recurse]` | Validates XML and imports into the current tab only. A directory is nonrecursive by default. Does not copy files into the user directory or execute their contents. |
| `Import-IseSnippet -Module <name> [-ListAvailable] [-Recurse]` | Searches each matching module's `Snippets` directory. Without `-ListAvailable`, the module must already be loaded. Does not import/execute the module. Missing modules raise errors. |

New snippets are stored under `Iseberg/Snippets` in local application data, **not** the legacy `Documents/WindowsPowerShell/Snippets` folder. On Windows, existing legacy snippets remain discoverable without being modified. Unlike ISE's historical execution-policy restriction on custom snippets, Iseberg treats snippet XML as data: loading it never executes its script text. Execution policy still applies when running scripts.

```powershell
New-IseSnippet -Title 'Log message' -Description 'Write an informational message' `
    -Text "Write-Information 'message'" -Author $env:USERNAME -CaretOffset 19
Get-IseSnippet | Select-Object Name, FullName
Import-IseSnippet -Path (Join-Path $HOME 'shared-snippets') -Recurse
$psISE.CurrentPowerShellTab.Snippets | Select-Object DisplayTitle, Description
```

Saved and imported snippets appear in **Ctrl+J** for their owning tab. The **Use default snippets** preference controls built-ins in both the picker and `Snippets` collection. The UI's **Import Snippets** action still persists XML for all tabs; the cmdlet and `Snippets.Load(path)` import into one session only. Exporting the custom catalog includes that tab's imports. Malformed XML, unsupported schema, invalid metadata, file/provider failures, and duplicate file creation report errors. A file/directory import validates all its files before adding any entries. The existing XML parser rejects DTDs/external entities.

## Unsupported surfaces and WPF boundary

PowerShell tab creation/closure through `PowerShellTabs.Add/Remove`, cross-tab `Invoke/InvokeSynchronous`, and WPF tool `VerticalAddOnTools.Add`/`HorizontalAddOnTools.Add`/`Clear` raise explicit unsupported-operation errors. Use the normal desktop actions or script-based menus instead. Other members not listed above are **not exposed**, including `Options`, `ConsolePane`, `CurrentVisibleHorizontalTool`, `CurrentVisibleVerticalTool`, writable tab names, editor outlining/matching APIs, and legacy ISE events.

Use `Set-StrictMode -Version Latest` in compatibility scripts so reading an absent member raises a missing-property error. This matters because PowerShell normally returns null for missing properties and suppresses exceptions thrown by .NET property getters. Unsupported method calls and property writes fail without strict mode. Iseberg does not return placeholder settings/controls that appear to work.

`$psISE` and the host snippet commands are local-only and are not installed on remote endpoints. Menu actions are disabled until the original local runspace is restored. Remoting, local/remote file transfers, and debugger UI retain their existing native Iseberg workflows.

**WPF tools are unsupported on Windows as well as Linux/macOS.** The [ISE tool contract](https://learn.microsoft.com/en-us/previous-versions/powershell/scripting/windows-powershell/ise/object-model/the-iseaddontoolcollection-object) expects WPF control types and Windows PowerShell ISE hosting assemblies. Avalonia controls are not WPF controls, and running an STA PowerShell thread does not supply a WPF dispatcher, visual tree, docking host, or ISE assemblies. Adding Windows-only interop would require a separate host/lifetime model and still would not make Windows PowerShell ISE binary extensions compatible with this PowerShell 7 host. No WPF assemblies are loaded or Windows-only target framework introduced for this compatibility layer. WPF controls cannot run natively on Linux or macOS.

Port script-only extensions using the supported editor/menu APIs above. WPF/binary ISE add-ons must be rewritten for Avalonia/Iseberg, or remain in Windows PowerShell ISE on Windows. This release does not define an Avalonia binary add-on plugin contract.

Member naming and command signatures are based on Microsoft's archived [ISE object model](https://learn.microsoft.com/en-us/previous-versions/powershell/scripting/windows-powershell/ise/object-model/the-ise-object-model-hierarchy) and [ISE module reference](https://learn.microsoft.com/en-us/powershell/module/ise/?view=powershell-5.1); the supported subset and differences on this page take precedence for Iseberg.
