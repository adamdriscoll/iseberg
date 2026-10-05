[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $Runtime,
    [Parameter(Mandatory)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw "Expected a version such as 1.2.3 or 1.2.3-preview.1, not '$Version'."
}
$numericVersion = ($Version -split '-')[0]
$parts = $numericVersion.Split('.')
if ([long]$parts[0] -gt 255 -or [long]$parts[1] -gt 255 -or [long]$parts[2] -gt 65535) {
    throw 'Package versions must fit MSI limits: major/minor <= 255 and patch <= 65535.'
}
if (($Runtime.StartsWith('win-') -and !$IsWindows) -or
    ($Runtime.StartsWith('osx-') -and !$IsMacOS) -or
    ($Runtime.StartsWith('linux-') -and !$IsLinux)) {
    throw "Package $Runtime on its corresponding operating system."
}

$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish' $Runtime $Version
$output = Join-Path $root 'publish' 'artifacts' $Runtime
$staging = Join-Path $root 'publish' 'staging' $Runtime $Version
foreach ($path in @($publish, $output, $staging)) {
    if (Test-Path -LiteralPath $path) { throw "Packaging output already exists: $path. Remove it before rebuilding." }
    New-Item -ItemType Directory -Path $path | Out-Null
}
dotnet publish (Join-Path $root 'src' 'Iseberg' 'Iseberg.csproj') -c Release -r $Runtime --self-contained true `
    "-p:Version=$Version" -p:PublishTrimmed=false -p:PublishAot=false -o $publish --verbosity minimal
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $publish
$name = "Iseberg-$Version-$Runtime"

if ($IsWindows) {
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $output "$name.zip")
    dotnet tool restore
    dotnet tool run wix -- extension add WixToolset.UI.wixext/5.0.2
    dotnet tool run wix -- build (Join-Path $PSScriptRoot 'Windows.wxs') -arch x64 `
        -d "PublishDir=$publish" -d "Version=$numericVersion" -ext WixToolset.UI.wixext/5.0.2 `
        -o (Join-Path $output "$name.msi")
    dotnet tool run wix -- msi validate (Join-Path $output "$name.msi")
} elseif ($IsMacOS) {
    $app = Join-Path $staging 'Iseberg.app'
    $contents = Join-Path $app 'Contents'
    $macos = Join-Path $contents 'MacOS'
    New-Item -ItemType Directory -Path $macos, (Join-Path $contents 'Resources') | Out-Null
    Copy-Item -Path (Join-Path $publish '*') -Destination $macos -Recurse
    $plist = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Info.plist') -Raw).
        Replace('@VERSION@', $Version).Replace('@NUMERIC_VERSION@', $numericVersion)
    Set-Content -LiteralPath (Join-Path $contents 'Info.plist') -Value $plist -Encoding utf8NoBOM
    chmod +x (Join-Path $macos 'Iseberg')
    New-Item -ItemType SymbolicLink -Path (Join-Path $staging 'Applications') -Target '/Applications' | Out-Null
    hdiutil create -volname Iseberg -srcfolder $staging -ov -format UDZO (Join-Path $output "$name.dmg")
    Push-Location $staging
    try { zip -q -r -y (Join-Path $output "$name.zip") 'Iseberg.app' }
    finally { Pop-Location }
} else {
    chmod +x (Join-Path $publish 'Iseberg')
    Push-Location $publish
    try { zip -q -r -y (Join-Path $output "$name.zip") '.' }
    finally { Pop-Location }
}
