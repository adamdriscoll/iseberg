# Iseberg

An original, cross-platform PowerShell ISE-style desktop workbench built with Avalonia, AvaloniaEdit, and PowerShell 7.6.6. The goal is the familiar ISE interaction model, not a web editor or a wrapper around Windows PowerShell.

**This is an initial working implementation, not a complete or pixel-perfect replacement for Microsoft PowerShell ISE.** See [the parity inventory](docs/parity.md) for implemented behavior and remaining gaps.

## Run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```powershell
dotnet run --project src/Iseberg
```

Open scripts from the command line:

```powershell
dotnet run --project src/Iseberg -- ./example.ps1
```

PowerShell is included through the SDK dependency; a separate `pwsh` installation is not required. Windows, Linux, and macOS are target platforms. Native desktop interaction has been exercised on Windows. A CI matrix is configured for all three platforms; Linux and macOS have not been verified locally, and the newly added workflow has not run yet.

On Linux, use a graphical desktop with Avalonia's native dependencies (including X11, fontconfig, and OpenGL or software rendering). Native title bars, file pickers, available fonts, and platform-specific cmdlets necessarily differ by operating system.

## Use

The default view has a white script editor above the blue console, a Commands pane on the right, and the familiar menu, toolbar, file tabs, PowerShell tabs, and status/zoom bar.

| Action | Shortcut |
|---|---|
| New / open / save script | Ctrl+N / Ctrl+O / Ctrl+S |
| Save as / close script | Ctrl+Shift+S / Ctrl+W |
| New / close PowerShell tab | Ctrl+T / Ctrl+Shift+W |
| Run script / continue debugging | F5 |
| Run selection, or current line | F8 |
| Stop execution | Ctrl+Break or Shift+F5 |
| Toggle breakpoint | F9 |
| Step over / into / out | F10 / F11 / Shift+F11 |
| Completion / snippets | Ctrl+Space / Ctrl+J |
| Find / replace / go to line | Ctrl+F / Ctrl+H / Ctrl+G |
| Script top / right / maximized | Ctrl+1 / Ctrl+2 / Ctrl+3 |
| Focus script / console | Ctrl+I / Ctrl+D |
| Clear console | Ctrl+L |

On macOS, Command is also accepted for the workbench shortcuts. Console Up/Down retrieves command history; Shift+Enter inserts a newline. Enter leaves syntactically incomplete commands open for more input. Each PowerShell tab has an independent persistent runspace, so variables, functions, current directory, and loaded modules survive successive commands without leaking into another tab.

**Windows execution policy is respected.** If local `.ps1` files are blocked, use **Tools > Enable Local Scripts (Process Only)** and explicitly approve `RemoteSigned`. This changes only the application process, affects all its runspaces, and does not override Group Policy or change user/machine settings. Running named files uses their real paths, preserving `$PSScriptRoot`, `$PSCommandPath`, and debugger source locations. Modified named scripts are saved before F5; untitled scripts can run in memory unless breakpoints require saving.

Profiles are opt-in under Tools > Options. `$PROFILE` points to `Iseberg_profile.ps1` in the usual user PowerShell configuration directory, alongside the shared `profile.ps1`. Loading profiles executes all four profile locations in normal PowerShell order. Startup never silently changes execution policy or executes user profiles.

Scripts execute with your account's permissions. This is **not a sandbox**. The console is a graphical PowerShell host, not a terminal emulator; full-screen interactive native programs and raw keyboard/buffer operations are not supported.

## Build and test

```powershell
dotnet build Iseberg.slnx
dotnet test Iseberg.slnx
dotnet publish src/Iseberg -c Release -r win-x64 --self-contained true -o ./publish/win-x64
```

Use `linux-x64`, `linux-arm64`, `osx-x64`, or `osx-arm64` as appropriate. Do not enable trimming or Native AOT: the PowerShell engine and module discovery depend on reflection and dynamic loading. Release builds omit Avalonia developer tooling.

The test suite exercises real PowerShell execution, session isolation, formatted output, input/cancellation, errors, command metadata, completion, script paths, breakpoints/stepping, file encodings, dirty state, parser folding, settings, and headless desktop construction.

## Visual Studio Code

Open the repository folder in VS Code and install the recommended **C#** and **Avalonia for VS Code** extensions.

- **F5** launches `Iseberg: Debug` after building the solution. **Ctrl+F5** uses the selected launch configuration without attaching the debugger.
- Select `Iseberg: Release` to launch an optimized build without Avalonia developer tools.
- Select `Iseberg: Debug current script` with a saved PowerShell script active in VS Code to open that file in Iseberg. C# breakpoints debug the application; PowerShell script breakpoints are managed inside Iseberg.
- Select `Iseberg: Attach` to choose an already-running application process.
- **Ctrl+Shift+B** runs the default Debug build. **Terminal > Run Task** also offers Release build, Release tests, and self-contained publishing with a target-platform picker.

Published files go into `publish/<runtime>` and are ignored by Git. Launch paths use VS Code's platform-specific path separator, so the same configurations work on Windows, Linux, and macOS.

## Architecture and provenance

- `src/Iseberg.Core`: persistent PowerShell host/runspace, execution, completion, debugging, parser analysis, file persistence, preferences.
- `src/Iseberg`: native Avalonia workbench, per-file text documents/undo, highlighting, console rendering, menus, dialogs, command explorer.
- `tests/Iseberg.Tests`: engine and headless desktop tests.

The installed Windows ISE assemblies were inspected with ILSpy to understand behavior and module responsibilities. The implementation is newly authored; it does not include decompiled Microsoft source, WPF controls, proprietary editor assemblies, icons, or other ISE assets. [Reference findings](docs/ise-reference.md) explain the architectural observations and intentional replacements.
