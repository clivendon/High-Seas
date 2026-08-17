param(
    [string]$Message = "Release High Seas Media",
    [string]$Remote,
    [switch]$SkipBuild,
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

function Invoke-Git([string[]]$Arguments) {
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed." }
}

if (-not (Test-Path -LiteralPath (Join-Path $root '.git'))) {
    Write-Host 'Initializing the local Git repository…'
    Invoke-Git @('init')
}

if (-not $Remote) {
    $Remote = (& git remote get-url origin 2>$null)
}
if ($Remote -and -not (& git remote 2>$null | Select-String '^origin$')) {
    Invoke-Git @('remote', 'add', 'origin', $Remote)
}

if (-not $SkipBuild) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build-release.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'The release build failed.' }
}

$versionFile = Join-Path $root 'phone-app\High-Seas-Remote-version.txt'
$version = if (Test-Path -LiteralPath $versionFile) { (Get-Content -LiteralPath $versionFile -Raw).Trim() } else { Get-Date -Format 'yyyy.MM.dd.HHmm' }
$tag = "v$version"
$apk = Join-Path $root "High-Seas-Remote-v$version.apk"
$latestApk = Join-Path $root 'phone-app\High-Seas-Remote-latest.apk'
$windowsZip = Join-Path $root "High-Seas-Media-v$version-Windows.zip"

if (-not (Test-Path -LiteralPath $apk) -and (Test-Path -LiteralPath $latestApk)) {
    Copy-Item -LiteralPath $latestApk -Destination $apk
}

Invoke-Git @('add', '-A')
$pending = (& git diff --cached --name-only)
if ($pending) {
    Invoke-Git @('commit', '-m', $Message)
} else {
    Write-Host 'No source changes to commit.'
}

if (& git tag --list $tag) { Write-Host "Tag $tag already exists; skipping tag creation." }
else { Invoke-Git @('tag', '-a', $tag, '-m', "High Seas Media $version") }

if (-not $Remote) {
    Write-Warning "No GitHub remote is configured. Add one with: git remote add origin <GitHub URL>"
    Write-Host "The local release is ready as $tag. Re-run this script after adding origin."
    exit 0
}

Invoke-Git @('push', '-u', 'origin', 'HEAD')
Invoke-Git @('push', 'origin', $tag)

$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($gh -and (Test-Path -LiteralPath $apk)) {
    $releaseArgs = @('release', 'create', $tag, $apk)
    if (Test-Path -LiteralPath $windowsZip) { $releaseArgs += $windowsZip }
    $releaseArgs += @('--title', "High Seas Media $version", '--generate-notes')
    if ($Draft) { $releaseArgs += '--draft' }
    & $gh.Source @releaseArgs
    if ($LASTEXITCODE -ne 0) { throw 'GitHub release creation failed.' }
} else {
    Write-Host "Code and tag pushed. Create a GitHub release for $tag and attach $apk."
}
