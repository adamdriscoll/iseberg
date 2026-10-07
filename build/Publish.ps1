param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $Runtime,
    [ValidateNotNullOrEmpty()]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$versionArguments = @()
$name = "Iseberg-$Runtime"
if ($Version) {
    $versionArguments = @("-p:Version=$Version")
    $name = "Iseberg-$Version-$Runtime"
}
$root = Split-Path $PSScriptRoot
$project = Join-Path (Join-Path $root 'src') 'Iseberg'
$destination = Join-Path $root 'publish'
$staging = Join-Path $destination (".staging-$Runtime-" + [guid]::NewGuid().ToString('N'))
$executable = if ($Runtime.StartsWith('win-')) { 'Iseberg.exe' } else { 'Iseberg' }

function Test-Runtime([string] $Path, [bool] $ExpectFailure = $false) {
    $start = [Diagnostics.ProcessStartInfo]::new($Path)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--check-runtime')
    if ($ExpectFailure) { $start.Environment['ISEBERG_PSHOME'] = Join-Path $staging 'missing-powershell' }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        $process.Start() | Out-Null
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(90000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Published runtime check timed out.'
        }
        $text = $output.GetAwaiter().GetResult() + $errorOutput.GetAwaiter().GetResult()
        if ($ExpectFailure) {
            if ($process.ExitCode -eq 0 -or $text -notmatch 'ISEBERG_PSHOME') {
                throw "Compact distribution did not reject a missing PowerShell installation: $text"
            }
        }
        elseif ($process.ExitCode -ne 0 -or $text -notmatch 'Iseberg runtime check passed:') {
            throw "Published runtime check failed (exit $($process.ExitCode)): $text"
        }
        else { Write-Host $text.Trim() }
    }
    finally { $process.Dispose() }
}

function New-DistributionArchive([string] $Directory, [string] $Path) {
    $temporary = "$Path.tmp"
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($Directory, $temporary)
        if (-not $Runtime.StartsWith('win-')) {
            $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Update)
            try {
                # Preserve Unix execute permissions when extracting either ZIP.
                $zip.GetEntry($executable).ExternalAttributes = (0x81ed -shl 16)
            }
            finally { $zip.Dispose() }
        }
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

New-Item -ItemType Directory -Path $staging -Force | Out-Null
Push-Location $root
try {
    $full = Join-Path $staging 'full'
    $compact = Join-Path $staging 'compact'
    & dotnet publish $project -c Release -r $Runtime --self-contained true -o $full --verbosity minimal @versionArguments
    if ($LASTEXITCODE -ne 0) { throw "Full publish failed with exit code $LASTEXITCODE." }
    & dotnet publish $project -c Release -r $Runtime -p:PublishProfile=Compact -o $compact --verbosity minimal @versionArguments
    if ($LASTEXITCODE -ne 0) { throw "Compact publish failed with exit code $LASTEXITCODE." }

    $files = @(Get-ChildItem -LiteralPath $compact -Recurse -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne $executable) {
        $unexpected = $files | ForEach-Object { [IO.Path]::GetRelativePath($compact, $_.FullName) }
        throw "Compact publish must contain exactly one executable. Found: $($unexpected -join ', ')"
    }
    Test-Runtime (Join-Path $full $executable)
    Test-Runtime (Join-Path $compact $executable)
    Test-Runtime (Join-Path $compact $executable) -ExpectFailure $true

    $fullZip = Join-Path $destination "$name.zip"
    $compactZip = Join-Path $destination "$name-compact.zip"
    New-DistributionArchive $full $fullZip
    New-DistributionArchive $compact $compactZip
    if ((Get-Item $compactZip).Length -ge (Get-Item $fullZip).Length) {
        throw 'Compact ZIP must be smaller than the bundled PowerShell ZIP.'
    }
    Get-Item $fullZip, $compactZip | Select-Object Name, Length
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
