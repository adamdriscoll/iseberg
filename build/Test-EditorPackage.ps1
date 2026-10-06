param([switch]$NativeSmoke, [switch]$RefreshConsumerLocks)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$feed = Join-Path $root 'publish\editor-feed'
$packages = Join-Path $root 'publish\editor-consumer-packages'
$editor = Join-Path $root 'src\Iseberg.Editor\Iseberg.Editor.csproj'
$tests = Join-Path $root 'examples\EditorPackage.Tests\EditorPackage.Tests.csproj'
$hostProject = Join-Path $root 'examples\EditorHost\EditorHost.csproj'

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

function Get-UpstreamLock([string]$project) {
    $lockPath = Join-Path (Split-Path $project -Parent) 'packages.lock.json'
    if (-not (Test-Path $lockPath)) { return '' }
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        $localPackage = $framework.Value.'PoshTools.Iseberg.Editor'
        if ($null -ne $localPackage) { $localPackage.contentHash = '<fresh-local-artifact>' }
    }
    return $lock | ConvertTo-Json -Depth 100 -Compress
}

Invoke-DotNet restore $editor --locked-mode --verbosity minimal
Invoke-DotNet pack $editor -c Release --no-restore -o $feed --verbosity minimal
# An isolated package cache ensures the actual freshly packed nupkg is consumed.
$editorCache = Join-Path $packages 'poshtools.iseberg.editor\0.1.0-preview.1'
if (Test-Path $editorCache) { Remove-Item -LiteralPath $editorCache -Recurse -Force }
foreach ($project in @($tests, $hostProject)) {
    $baseline = Get-UpstreamLock $project
    Invoke-DotNet restore $project --force-evaluate --packages $packages --configfile (Join-Path $root 'examples\NuGet.Config') --verbosity minimal
    if (-not $RefreshConsumerLocks -and $baseline -ne (Get-UpstreamLock $project)) {
        throw 'Consumer upstream dependency lock changed. Review it and rerun with -RefreshConsumerLocks if intended.'
    }
    Invoke-DotNet restore $project --locked-mode --packages $packages --configfile (Join-Path $root 'examples\NuGet.Config') --verbosity minimal
}
Invoke-DotNet test $tests -c Release --no-restore --verbosity minimal --logger trx --results-directory (Join-Path $root 'TestResults\EditorPackage')
Invoke-DotNet build $hostProject -c Release --no-restore --verbosity minimal
if ($NativeSmoke) {
    if (-not $IsWindows) { throw 'Native smoke currently covers Windows only.' }
    Invoke-DotNet run --project $hostProject -c Release --no-build -- --smoke
}
