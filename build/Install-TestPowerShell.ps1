param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $Runtime
)

$ErrorActionPreference = 'Stop'
$project = [xml](Get-Content (Join-Path $PSScriptRoot '..\src\Iseberg.Core\Iseberg.Core.csproj') -Raw)
$version = $project.SelectSingleNode('//PackageReference[@Include="Microsoft.PowerShell.SDK"]').Version
$directory = Join-Path $env:RUNNER_TEMP "iseberg-powershell-$Runtime"
$extension = if ($Runtime.StartsWith('win-')) { 'zip' } else { 'tar.gz' }
$prefix = if ($Runtime.StartsWith('win-')) { 'PowerShell' } else { 'powershell' }
$asset = "$prefix-$version-$Runtime.$extension"
$archive = Join-Path $env:RUNNER_TEMP $asset
$url = "https://github.com/PowerShell/PowerShell/releases/download/v$version"
Invoke-WebRequest "$url/$asset" -OutFile $archive
$hashFile = Join-Path $env:RUNNER_TEMP 'iseberg-powershell-hashes.sha256'
Invoke-WebRequest "$url/hashes.sha256" -OutFile $hashFile
$hashes = Get-Content -LiteralPath $hashFile -Raw
$expected = ($hashes -split '\r?\n' | Where-Object { $_ -match ([regex]::Escape($asset) + '$') }) -split '\s+' | Select-Object -First 1
if (-not $expected -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $expected) {
    throw "PowerShell archive checksum verification failed for $asset."
}
New-Item -ItemType Directory -Path $directory -Force | Out-Null
if ($Runtime.StartsWith('win-')) {
    Expand-Archive -LiteralPath $archive -DestinationPath $directory -Force
}
else {
    & tar -xzf $archive -C $directory
    if ($LASTEXITCODE -ne 0) { throw "PowerShell archive extraction failed with exit code $LASTEXITCODE." }
    & chmod +x (Join-Path $directory 'pwsh')
    if ($LASTEXITCODE -ne 0) { throw "Setting pwsh executable permissions failed with exit code $LASTEXITCODE." }
}
$directory | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append
"ISEBERG_PSHOME=$directory" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
