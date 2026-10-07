# Development guide

[Back to README](../README.md)

Run all commands below from the repository root.

## Contents

- [Platform support](#platform-support)
- [Build and test](#build-and-test)
- [Publish](#publish)
- [Visual Studio Code](#visual-studio-code)
- [Project structure](#project-structure)

## Platform support

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build or run from source. PowerShell is included through the SDK dependency; a separate `pwsh` installation is not required for the default application build.

Windows, Linux, and macOS are target platforms. Engine/headless desktop tests and publishing have been verified locally on Windows and Linux, and CI runs the same checks on all three platforms. Native desktop interaction has been exercised on Windows; native Linux/macOS desktop interaction has not yet been verified.

On Linux, use a graphical desktop with Avalonia's native dependencies (including X11, fontconfig, and OpenGL or software rendering). Native title bars, file pickers, available fonts, and platform-specific cmdlets necessarily differ by operating system.

## Build and test

```powershell
dotnet build Iseberg.slnx
dotnet test Iseberg.slnx
```

The app and its shared editor use Avalonia/Desktop/Fluent 12.1.3 and AvaloniaEdit 12.0.0. The default workbench build includes PowerShell and must remain untrimmed; editor-only hosts have no PowerShell dependency. Release builds omit Avalonia developer tooling.

### Test coverage and prerequisites

The test suite exercises real PowerShell execution, session isolation, formatted output, input/cancellation, errors, command metadata, completion, script paths, breakpoints/stepping, file encodings, dirty state, parser folding, settings, and headless desktop construction.

Compact installation checks require compatible PowerShell as described under [compact distribution requirements](#compact-single-file-distribution). Remoting tests require `pwsh` on `PATH`: they launch isolated child processes and connect over PowerShell's named-pipe remoting transport, without needing an SSH/WSMan server.

Remoting tests exercise interactive `Enter-PSSession`, remote files, completion, debugger inspection/source navigation, stopping, and connection loss. SSH/WSMan authentication and external-server configuration still need environment-specific verification.

## Publish

| Distribution | Contents | PowerShell requirement |
|---|---|---|
| Full | Self-contained application with bundled PowerShell | No separate installation |
| Compact | One compressed, self-contained executable without the PowerShell engine, modules, or its third-party dependencies | Compatible installed PowerShell |

### Full distribution

```powershell
dotnet publish src/Iseberg -c Release -r win-x64 --self-contained true -o ./publish/win-x64
```

Use `linux-x64`, `linux-arm64`, `osx-x64`, or `osx-arm64` as appropriate. The full distribution must remain untrimmed.

Windows full builds and publishes copy `packaging/pwsh_ise.cmd` beside `Iseberg.exe`, so both ZIP packaging paths and the MSI include the same command-line launcher. The MSI appends the installation folder to the system `PATH` and removes that entry on uninstall. The launcher is excluded from compact and non-Windows publishes.

### Platform packages

To create distribution packages, use PowerShell 7 on the corresponding OS:

```powershell
./packaging/Package.ps1 -Runtime win-x64 -Version 1.2.3
```

The script publishes self-contained files and writes versioned packages to `publish/artifacts/<runtime>`. Windows restores the pinned WiX tool; macOS uses `hdiutil` and `zip`; Linux uses `zip`. Each run requires fresh output directories.

macOS ZIPs contain the full `.app` bundle; Unix ZIPs preserve executable permissions. The original `dotnet publish` path is unchanged.

### Release workflow

CI builds full and compact packages for all five runtimes after the existing platform tests, using `0.0.<run-number>-ci` versions, and uploads the MSI/DMG/ZIP files and NuGet packages as Actions artifacts. Both CI and releases use the same reusable packaging workflow, including native compact runtime checks. CI does not publish to nuget.org.

**Release packages** runs when a version tag (`v1.2.3` or `1.2.3`, optionally with a prerelease suffix) is pushed or a release is published. It builds from that tag, stamps both distributions and NuGet packages with the release version, and attaches the full packages, compact ZIPs, and `.nupkg` files directly to the release after all packaging jobs succeed. After uploading the release assets, it publishes `PoshTools.Iseberg.Core`, `PoshTools.Iseberg.Editor` and `PoshTools.ISEBerg` to nuget.org.

A tag push creates a release if needed; prerelease tags create prereleases. Publishing an existing release preserves its title/body. The workflow needs `contents: write` only for uploading/creating releases.

Version numbers must fit MSI limits (major/minor at most 255, patch at most 65535). Full release assets use `Iseberg-<version>-<runtime>.<extension>`, which the update checker also uses. Compact release assets use `Iseberg-<version>-<runtime>-compact.zip`.

### NuGet packages

Releases use [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), not a stored API key. Configure a trusted publishing policy on nuget.org with these GitHub settings:

| Setting | Value |
|---|---|
| Repository owner | `adamdriscoll` |
| Repository | `iseberg` |
| Workflow file | `release.yml` (filename only, not `.github/workflows/release.yml`) |
| Environment | Leave empty; the publishing job does not use a GitHub Actions environment |

Choose the NuGet package owner and publishing scopes that cover `PoshTools.ISEBerg`, `PoshTools.Iseberg.Core` and `PoshTools.Iseberg.Editor`, including creating new packages for their first release. Add a **repository Actions secret** named `NUGET_USER` containing your nuget.org **profile username**, not your email address. A stored `NUGET_API_KEY` secret is no longer needed and can be removed.

Only the publishing job has `id-token: write`. After downloading the built packages, `NuGet/login@v1` exchanges the GitHub OIDC token for a temporary API key, valid for one hour, immediately before pushing. Missing usernames or mismatched/inactive trusted policies fail authentication. Already-published package versions are skipped so tag/release events and workflow retries do not attempt to overwrite immutable NuGet versions.

All packages target .NET 10. `PoshTools.ISEBerg` provides the embeddable Avalonia `WorkbenchControl` and depends on the same release versions of `PoshTools.Iseberg.Core`, which contains the PowerShell engine, and `PoshTools.Iseberg.Editor`, which contains the shared engine-independent editor. The assembly names and C# namespaces are `Iseberg`, `Iseberg.Core` and `Iseberg.Editor`. The [NuGet hosting guide](nuget-hosting.md) documents installation, configuration, public operations, persistence, and lifetime management.

To build the same packages locally without publishing:

```powershell
dotnet pack Iseberg.slnx -c Release -p:Version=1.2.3 -p:ContinuousIntegrationBuild=true -o publish/nuget
```

The output contains `PoshTools.ISEBerg.1.2.3.nupkg`, `PoshTools.Iseberg.Core.1.2.3.nupkg` and `PoshTools.Iseberg.Editor.1.2.3.nupkg`. Prerelease tags retain their suffix in the NuGet version. The release workflow publishes Core and Editor before their dependent UI package.

Iseberg's script pane and Options preview use `PoshTools.Iseberg.Editor`, and the protected console shares its accessible text view, scoped styles and completion popup. There is no second workbench editing control. Workbench-specific adapters retain parser colors/diagnostics, folding and debugger adornments without introducing runtime dependencies into the editor package. The editor producer and independent consumers have committed dependency locks; restore with `--locked-mode`. Verify the default preview through actual package-only Fluent consumers with `.\build\Test-EditorPackage.ps1`; add `-NativeSmoke` on Windows. See the [editor hosting guide](editor-hosting.md) for its interface, extension seam, platform/license limits and host ownership. Packaging does not itself publish anything.

### Compact single-file distribution

The `Compact` publish profile produces one compressed, self-contained executable without bundling the PowerShell engine, modules, or its third-party dependencies. Install **PowerShell 7.6.6 or a newer stable 7.6.x patch**, using **.NET 10.0** and the **same architecture** as Iseberg. A separate .NET installation is not required: the executable includes its own runtime.

Iseberg discovers `pwsh` on `PATH`, without loading profiles. To select another installation, set `ISEBERG_PSHOME` to the directory containing `pwsh`/`pwsh.exe`, `pwsh.dll`, and `System.Management.Automation.dll`. Missing or incompatible installations produce a startup error rather than silently selecting another engine.

```powershell
dotnet publish src/Iseberg -c Release -r win-x64 -p:PublishProfile=Compact -o ./publish/compact-win-x64
./publish/compact-win-x64/Iseberg.exe --check-runtime | Out-Host
```

Use the matching runtime identifier on Linux/macOS and run `Iseberg` instead of `Iseberg.exe`.

### Runtime verification

`--check-runtime` checks real runspace execution, module loading, JSON cmdlets, `Add-Type`, command discovery/forms/help, completion, parsing, breakpoint inspection/resume, and settings serialization without opening the desktop. It returns a nonzero exit code on failure and also works with the full distribution.

### Trimming and extraction

Compact trimming is deliberately partial: Avalonia's trim-compatible libraries are trimmed, while the application's reflection-bound models, AvaloniaEdit, and .NET framework APIs used by dynamically loaded scripts are preserved. Compiler reference assemblies are included for `Add-Type`. NativeAOT is not supported because this in-process PowerShell host requires dynamic assembly loading and code generation.

Compact publishing excludes external `.pdb` debug-symbol sidecars, including those supplied by Windows Skia/HarfBuzz native packages, to preserve its single-executable contract. Native runtime binaries remain bundled; normal full publishes retain their package-supplied debug symbols.

At launch the single file extracts its bundled assemblies/native libraries into .NET's extraction cache. The cache must be writable; set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to change its location. Keep the selected PowerShell installation available while Iseberg runs. All-users profile paths come from that engine installation; user profiles remain opt-in.

### Compact ZIP packaging and CI artifacts

To create both ZIP distributions on a matching OS/architecture with compatible PowerShell installed:

```powershell
./build/Publish.ps1 -Runtime win-x64
```

Without `-Version`, the script writes `publish/Iseberg-<runtime>.zip` and `publish/Iseberg-<runtime>-compact.zip`. Add `-Version 1.2.3` to stamp both applications and write versioned ZIP names, including `publish/Iseberg-1.2.3-<runtime>-compact.zip`.

CI and releases build and check compact distributions natively for `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`, uploading their ZIPs as separate Actions artifacts. Each compact-package platform artifact contains `Iseberg-<version>-<runtime>-compact.zip` (one executable, installed PowerShell required).

Packaging verifies the compact ZIP is smaller and preserves Unix executable permissions.

## Visual Studio Code

Open the repository folder in VS Code and install the recommended **C#** and **Avalonia for VS Code** extensions.

| Configuration or action | Behavior |
|---|---|
| `Iseberg: Debug` | **F5** launches after building the solution. **Ctrl+F5** uses the selected launch configuration without attaching the debugger. |
| `Iseberg: Release` | Launches an optimized build without Avalonia developer tools. |
| `Iseberg: Debug current script` | Opens the saved PowerShell script active in VS Code in Iseberg. C# breakpoints debug the application; PowerShell script breakpoints are managed inside Iseberg. |
| `Iseberg: Attach` | Lets you choose an already-running application process. |
| **Ctrl+Shift+B** | Runs the default Debug build. |
| **Terminal > Run Task** | Offers Release build, Release tests, and self-contained publishing with a target-platform picker. |

Published files go into `publish/<runtime>` and are ignored by Git. Launch paths use VS Code's platform-specific path separator, so the same configurations work on Windows, Linux, and macOS.

## Project structure

| Path | Responsibility |
|---|---|
| `src/Iseberg.Core` | Persistent PowerShell host/runspace, execution, completion, debugging, parser analysis, file persistence, preferences |
| `src/Iseberg` | Embeddable Avalonia workbench and standalone window, per-file text documents/undo, highlighting, console rendering, menus, dialogs, command explorer |
| `tests/Iseberg.Tests` | Engine and headless desktop tests |

See [the architecture overview](architecture.md) for module responsibilities and design decisions.
