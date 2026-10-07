# Package-only Fluent editor host

This example uses `PoshTools.Iseberg.Editor` through **PackageReference**, never a project reference. It owns its Application, Fluent theme and TextDocument. The toolbar captures text only; no script runs.

From the repo root:

```powershell
.\build\Test-EditorPackage.ps1
.\build\Test-EditorPackage.ps1 -NativeSmoke
dotnet run --project examples\EditorHost -c Release --no-restore
```

The verification script packs the producer, uses a local feed plus nuget.org for upstream dependencies, and restores both independent consumers with a separate package cache and locked dependencies. `EditorPackage.Tests` runs the public-interface headless Fluent tests against the **nupkg**. The consumers are intentionally outside the solution so a normal solution restore does not require an unpublished package.

`examples\NuGet.Config` source-maps this package to the local feed. The script refreshes only the local preview artifact's content hash (which changes with source/docs/git metadata), refuses any other lock change, then verifies locked restore. Use `-RefreshConsumerLocks` when intentionally changing upstream dependencies and review both lock files before committing. The historical local-artifact hashes in the locks are verification receipts, not portable nuget.org artifacts.

`--smoke` creates a native Windows Desktop/Fluent window and exercises layout, focused editing, synthetic undo/completion gestures, the Edit/Value automation peer, read-only behavior, lexical highlighting, explicit diagnostics-unavailable state, DSC/invalid/ordinary text, detach/reattach and host-owned text. It checks that no Iseberg app/Core or PowerShell runtime assembly loaded. Native smoke exits nonzero on failure. This desktop host has no PowerShell dependency.

Headless tests use Skia to assert an actual rendered frame and exercise keyboard event routing, find, undo/redo, selection, clipboard paths, injected diagnostics/completion cancellation/failure/staleness and lifetime. The headless test project explicitly supplies PowerShell solely to preserve an existing host-owned unopened default runspace and observe the creation counter, including DSC text; it never calls Parser.ParseInput or opens a runspace. That dependency is test-only, not in the producer or desktop host, and its output is not redistributable as an editor package without separate license review.

Native input uses Avalonia's text-input path and synthetic key routing, not physical keyboard hardware. Actual Windows UI Automation client/screen-reader behavior and native Linux/macOS interaction remain unverified; do not infer them from a headless pass.

No Runspace integration, arbitrary script execution seam, debugger, prompt/output/history/session ownership, file persistence, NuGet publication, or desktop bootstrap is provided by this example.
