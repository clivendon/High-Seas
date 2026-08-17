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
    # GitHub Desktop installs Git Credential Manager even when the gh CLI is absent. Use the
    # already-authenticated credential only in memory to create the release and upload its assets.
    $credential = ("protocol=https`nhost=github.com`n`n" | git credential fill) -join "`n"
    $token = ([regex]::Match($credential, '(?m)^password=(.+)$')).Groups[1].Value.Trim()
    $remoteMatch = [regex]::Match($Remote, 'github\.com[/:](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?$')
    if ([string]::IsNullOrWhiteSpace($token) -or -not $remoteMatch.Success) {
        Write-Host "Code and tag pushed. Install/sign in to GitHub CLI to create a release for $tag and attach $apk."
    } else {
        $repoPath = "$($remoteMatch.Groups['owner'].Value)/$($remoteMatch.Groups['repo'].Value)"
        $headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
        $release = $null
        try { $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repoPath/releases/tags/$tag" -Headers $headers -Method Get } catch { }
        if (-not $release) {
            $body = @{ tag_name = $tag; name = "High Seas Media $version"; generate_release_notes = $true; draft = [bool]$Draft } | ConvertTo-Json
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repoPath/releases" -Headers $headers -Method Post -ContentType 'application/json' -Body $body
        }
        $assets = @($release.assets | ForEach-Object name)
        foreach ($assetPath in @($apk, $windowsZip)) {
            if (-not (Test-Path -LiteralPath $assetPath)) { continue }
            $assetName = [IO.Path]::GetFileName($assetPath)
            if ($assets -contains $assetName) { Write-Host "Release asset already exists: $assetName"; continue }
            $contentType = if ($assetName.EndsWith('.apk')) { 'application/vnd.android.package-archive' } else { 'application/zip' }
            Write-Host "Uploading $assetName…"
            Invoke-WebRequest -Uri "$($release.upload_url -replace '\{\?.*\}$','')?name=$([uri]::EscapeDataString($assetName))" -Headers $headers -Method Post -InFile $assetPath -ContentType $contentType | Out-Null
        }
        Write-Host "GitHub release ready: $($release.html_url)"
    }
}
