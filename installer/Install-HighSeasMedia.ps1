param([switch]$NoLaunch)

$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = if (Test-Path -LiteralPath (Join-Path $packageRoot 'CliveMediaCenter.exe')) {
    $packageRoot
} elseif (Test-Path -LiteralPath (Join-Path $packageRoot 'publish\CliveMediaCenter.exe')) {
    Join-Path $packageRoot 'publish'
} else {
    throw 'This installer must be run from a High Seas Media release package.'
}

$installer = Join-Path $releaseRoot 'install-release.ps1'
if (-not (Test-Path -LiteralPath $installer)) {
    $installer = Join-Path (Split-Path -Parent $releaseRoot) 'install-release.ps1'
}
if (-not (Test-Path -LiteralPath $installer)) { throw 'The release installer files are incomplete.' }

$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-PublishFolder', $releaseRoot)
if (-not $NoLaunch) { $arguments += '-Launch' }
& powershell.exe @arguments
if ($LASTEXITCODE -ne 0) { throw 'High Seas Media could not be installed.' }

Write-Host ''
Write-Host 'High Seas Media is installed for this Windows user.' -ForegroundColor Green
Write-Host 'Use Start > All apps > High Seas Media to launch it.'

