[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Title,

    [string]$Path,

    [ValidateRange(1, 16)]
    [int]$Monitor = 3,

    [string]$SubtitlePath,

    [switch]$ForceResync,

    [switch]$AllowNoSubtitles,

    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$videoExtensions = @('.mkv', '.mp4', '.avi', '.mov', '.m4v', '.wmv', '.webm')
$movieRoots = @(
    'D:\Movies',
    'D:\Shows',
    (Join-Path ([Environment]::GetFolderPath('MyVideos')) 'Movies'),
    (Join-Path ([Environment]::GetFolderPath('MyVideos')) 'Shows')
)

$ffsubsync = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tools\ffsubsync') -Filter 'ffsubsync.exe' -File -Recurse |
    Select-Object -First 1 -ExpandProperty FullName
$ffmpeg = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tools\ffmpeg') -Filter 'ffmpeg.exe' -File -Recurse |
    Select-Object -First 1 -ExpandProperty FullName
$ffprobe = Join-Path (Split-Path -Parent $ffmpeg) 'ffprobe.exe'
$vlc = 'C:\Program Files\VideoLAN\VLC\vlc.exe'

foreach ($requiredFile in @($ffsubsync, $ffmpeg, $ffprobe, $vlc)) {
    if (-not $requiredFile -or -not (Test-Path -LiteralPath $requiredFile)) {
        throw "Required executable is missing: $requiredFile"
    }
}

if ($Path) {
    $movie = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($videoExtensions -notcontains $movie.Extension.ToLowerInvariant()) {
        throw "Unsupported media file: $Path"
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($Title)) {
        throw 'Supply either -Title or -Path.'
    }
    $titlePattern = [regex]::Escape($Title)
    $matches = @(
        foreach ($root in $movieRoots) {
            if (Test-Path -LiteralPath $root) {
                Get-ChildItem -LiteralPath $root -File -Recurse -ErrorAction SilentlyContinue |
                    Where-Object {
                        $videoExtensions -contains $_.Extension.ToLowerInvariant() -and
                        $_.BaseName -match $titlePattern
                    }
            }
        }
    )

    if ($matches.Count -ne 1) {
        $candidateList = ($matches | ForEach-Object FullName) -join [Environment]::NewLine
        throw "Expected one movie matching '$Title', found $($matches.Count).`n$candidateList"
    }
    $movie = $matches[0]
}
$basePath = Join-Path $movie.DirectoryName $movie.BaseName
$activeSubtitle = "$basePath.srt"
$sourceSubtitle = "$basePath.source.srt"
$markerPath = "$basePath.subtitle-sync.json"
$temporarySubtitle = "$basePath.syncing.srt"

$probeJson = & $ffprobe -v error -show_entries 'stream=index,codec_type,codec_name:stream_tags=language,title' -of json -- $movie.FullName |
    Out-String
$probe = $probeJson | ConvertFrom-Json
$englishEmbedded = @(
    $probe.streams | Where-Object {
        $_.codec_type -eq 'subtitle' -and
        ($_.tags.language -match '^(eng|en)$' -or $_.tags.title -match 'English')
    }
)

$subtitleMode = 'none'
$syncStatus = 'not needed'

if ($SubtitlePath) {
    $resolvedSubtitle = (Resolve-Path -LiteralPath $SubtitlePath).Path
    Copy-Item -LiteralPath $resolvedSubtitle -Destination $sourceSubtitle -Force
}
elseif (-not (Test-Path -LiteralPath $sourceSubtitle) -and (Test-Path -LiteralPath $activeSubtitle)) {
    Copy-Item -LiteralPath $activeSubtitle -Destination $sourceSubtitle
}

if (Test-Path -LiteralPath $sourceSubtitle) {
    $subtitleMode = 'external English SRT'
    $sourceHash = (Get-FileHash -LiteralPath $sourceSubtitle -Algorithm SHA256).Hash
    $videoFingerprint = "$($movie.Length):$($movie.LastWriteTimeUtc.Ticks)"
    $cached = $false

    if (-not $ForceResync -and (Test-Path -LiteralPath $markerPath) -and (Test-Path -LiteralPath $activeSubtitle)) {
        try {
            $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
            $activeHash = (Get-FileHash -LiteralPath $activeSubtitle -Algorithm SHA256).Hash
            $cached = (
                $marker.videoFingerprint -eq $videoFingerprint -and
                $marker.sourceSha256 -eq $sourceHash -and
                $marker.outputSha256 -eq $activeHash
            )
        }
        catch {
            $cached = $false
        }
    }

    if ($cached) {
        $syncStatus = 'cached audio-synchronized subtitle'
    }
    else {
        Remove-Item -LiteralPath $temporarySubtitle -Force -ErrorAction SilentlyContinue
        $ffmpegDirectory = Split-Path -Parent $ffmpeg
        # Windows PowerShell 5 surfaces native stderr as an ErrorRecord. FFsubsync
        # writes normal progress messages there, so keep them without treating them
        # as terminating PowerShell errors.
        $previousErrorAction = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $syncOutput = & $ffsubsync $movie.FullName `
                -i $sourceSubtitle `
                -o $temporarySubtitle `
                --ffmpeg-path $ffmpegDirectory `
                --split-penalty 8 `
                --skip-sync-on-low-quality `
                --quality-max-offset-seconds 60 2>&1
            $syncExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorAction
        }
        $syncText = ($syncOutput | ForEach-Object ToString) -join [Environment]::NewLine
        Write-Host $syncText

        if ($syncExitCode -ne 0 -or -not (Test-Path -LiteralPath $temporarySubtitle)) {
            throw "Subtitle synchronization failed with exit code $syncExitCode."
        }
        if ($syncText -match 'low-quality alignment') {
            Remove-Item -LiteralPath $temporarySubtitle -Force
            throw 'Subtitle synchronization confidence was too low; the source subtitle was left untouched.'
        }

        Move-Item -LiteralPath $temporarySubtitle -Destination $activeSubtitle -Force
        $outputHash = (Get-FileHash -LiteralPath $activeSubtitle -Algorithm SHA256).Hash
        [ordered]@{
            video = $movie.FullName
            videoFingerprint = $videoFingerprint
            source = $sourceSubtitle
            sourceSha256 = $sourceHash
            outputSha256 = $outputHash
            synchronizedUtc = [DateTime]::UtcNow.ToString('o')
            tool = 'ffsubsync 0.5.1'
        } | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
        $syncStatus = 'newly audio-synchronized subtitle'
    }
}
elseif ($englishEmbedded.Count -gt 0) {
    $subtitleMode = 'embedded English subtitle'
    $syncStatus = 'container-matched; no synchronization required'
}
else {
    if ($AllowNoSubtitles) {
        $subtitleMode = 'none'
        $syncStatus = 'subtitles skipped'
    }
    else {
        throw "No English subtitle is available for '$($movie.BaseName)'. Supply a candidate with -SubtitlePath."
    }
}

if (-not $NoLaunch) {
    Get-Process -Name vlc -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400

    $vlcArguments = @(
        '--fullscreen'
        "--qt-fullscreen-screennumber=$($Monitor - 1)"
        '--play-and-exit'
        '--sub-language=eng'
    )
    if (Test-Path -LiteralPath $activeSubtitle) {
        $vlcArguments += '--sub-file="' + $activeSubtitle + '"'
    }
    $vlcArguments += '"' + $movie.FullName + '"'

    Start-Process -FilePath $vlc -ArgumentList $vlcArguments
    Start-Sleep -Seconds 3
    $vlcProcess = @(Get-Process -Name vlc -ErrorAction SilentlyContinue)
    if ($vlcProcess.Count -eq 0) {
        throw 'VLC exited before playback began.'
    }
}

[pscustomobject]@{
    Movie = $movie.BaseName
    Monitor = $Monitor
    Subtitle = $subtitleMode
    Synchronization = $syncStatus
    Launched = -not $NoLaunch
}
