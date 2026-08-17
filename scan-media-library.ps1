[CmdletBinding()]
param(
    [string]$OutputJson
)

$ErrorActionPreference = 'Stop'
if (-not $OutputJson) { $OutputJson = Join-Path $PSScriptRoot 'media-library.json' }
$ffprobe = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tools\ffmpeg') -Filter 'ffprobe.exe' -File -Recurse |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $ffprobe) { throw 'Portable ffprobe was not found.' }

$libraries = @(
    [pscustomobject]@{ Root = 'D:\Movies'; Kind = 'Movies' },
    [pscustomobject]@{ Root = 'D:\shows'; Kind = 'Shows' },
    [pscustomobject]@{ Root = (Join-Path ([Environment]::GetFolderPath('MyVideos')) 'Movies'); Kind = 'Movies' },
    [pscustomobject]@{ Root = (Join-Path ([Environment]::GetFolderPath('MyVideos')) 'Shows'); Kind = 'Shows' }
)
$videoExtensions = @('.mkv', '.mp4', '.avi', '.mov', '.m4v', '.wmv', '.webm', '.mpg', '.mpeg', '.ts', '.m2ts')

function Get-CleanTitle {
    param([string]$BaseName, [string]$MediaType)

    $name = $BaseName -replace '^\d{2}\s+', ''
    $name = $name -replace '^\[www\.Movcr\.to\]\s*-?', ''
    $name = $name -replace '\.', ' '
    if ($MediaType -eq 'Show') {
        if ($name -match '^Police Squad') { return 'Police Squad!' }
        if ($name -match '^Justified') { return 'Justified' }
        if ($name -match '^Marvels The Punisher') { return "Marvel's The Punisher" }
        if ($name -match '^Silo') { return 'Silo' }
    }
    if ($name -match '^(.*?)(?:\s+-\s+Comedy\s+|\s+-\s+TMNT-\d|\s+(?:19|20)\d{2}\b)') {
        $name = $matches[1]
    }
    $name = $name -replace '\s+(?:EXTENDED|REMASTERED|UNRATED|DC|DIRECTOR.?S CUT|BluRay|BrRip|BDRip|WEB.?DL|720p|1080p|2160p).*$', ''
    $name = $name -replace '\s+\[.*$', ''
    return ($name -replace '\s{2,}', ' ').Trim(' ', '-', '_')
}

$records = [System.Collections.Generic.List[object]]::new()
foreach ($library in $libraries) {
    if (-not (Test-Path -LiteralPath $library.Root)) { continue }
    $files = Get-ChildItem -LiteralPath $library.Root -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $videoExtensions -contains $_.Extension.ToLowerInvariant() }

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($library.Root.TrimEnd('\').Length).TrimStart('\')
        $relativeParts = $relative -split '\\'
        $collection = if ($relativeParts.Count -gt 1) { $relativeParts[0] } else { 'Standalone' }
        $isPoliceSquad = $file.BaseName -match '^0[1-6] Police Squad'
        $isBonus = $collection -match '^Of Mice and Men' -and $file.BaseName -match '^(Deleted Scenes|In Conversation|Make-up Tests|Making of|Screen Tests|Trailer)(?:\s|$)'
        $mediaType = if ($isBonus) { 'Bonus' } elseif ($library.Kind -eq 'Shows' -or $isPoliceSquad) { 'Show' } else { 'Movie' }

        $probeText = & $ffprobe -v error -show_entries 'format=duration,format_name:stream=index,codec_type,codec_name,width,height,channels,channel_layout:stream_tags=language,title' -of json -- $file.FullName |
            Out-String
        $probe = $probeText | ConvertFrom-Json
        $video = @($probe.streams | Where-Object codec_type -eq 'video') | Select-Object -First 1
        $audio = @($probe.streams | Where-Object codec_type -eq 'audio') | Select-Object -First 1
        $subtitles = @($probe.streams | Where-Object codec_type -eq 'subtitle')
        $englishEmbedded = @($subtitles | Where-Object { $_.tags.language -match '^(eng|en)$' -or $_.tags.title -match 'English' })

        $sidecars = @(Get-ChildItem -LiteralPath $file.DirectoryName -File -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Extension -match '^\.(srt|ass|ssa|sub|vtt)$' -and
                $_.BaseName.StartsWith($file.BaseName, [StringComparison]::OrdinalIgnoreCase)
            })
        $activeExternal = @($sidecars | Where-Object { $_.BaseName -eq $file.BaseName })
        $syncMarker = "$([IO.Path]::Combine($file.DirectoryName, $file.BaseName)).subtitle-sync.json"
        $subtitleStatus = if (Test-Path -LiteralPath $syncMarker) {
            'Audio-synced external'
        }
        elseif ($activeExternal.Count -gt 0) {
            'External present; unverified'
        }
        elseif ($englishEmbedded.Count -gt 0) {
            'Embedded English'
        }
        elseif ($subtitles.Count -gt 0) {
            'Embedded; language unclear'
        }
        else {
            'Missing English subtitle'
        }

        $season = $null
        $episode = $null
        if ($file.BaseName -match '(?i)S(\d{1,2})E(\d{1,2})') {
            $season = [int]$matches[1]
            $episode = [int]$matches[2]
        }
        elseif ($isPoliceSquad -and $file.BaseName -match '^(\d{2})') {
            $season = 1
            $episode = [int]$matches[1]
        }

        $year = $null
        if ($file.BaseName -match '\b((?:19|20)\d{2})\b') { $year = [int]$matches[1] }
        $cleanTitle = Get-CleanTitle -BaseName $file.BaseName -MediaType $mediaType

        $issues = [System.Collections.Generic.List[string]]::new()
        if ($subtitleStatus -eq 'Missing English subtitle') { $issues.Add('Missing English subtitle') }
        if ($file.BaseName -match '(?i)(www\.|YIFY|YTS|GalaxyRG|GalaxyTV|MovCR|BONE|TGx|BluRay|BrRip|BDRip|WEBRip|WEB.?DL|x26[45]|HEVC|H264|AAC|DDP?5\.1|1080p|720p)') {
            $issues.Add('Release tags in filename')
        }
        if ($file.FullName.Length -gt 240) { $issues.Add('Path exceeds 240 characters') }
        if ($video.height -and $video.width -and -not ([int]$video.height -ge 1000 -or [int]$video.width -ge 1900)) {
            $issues.Add('Below 1080p-class dimensions')
        }
        if (-not $year -and $mediaType -eq 'Movie' -and $collection -eq 'Standalone') { $issues.Add('Year not detected') }
        if ($file.BaseName -match '^Gattaca[.]1997') { $issues.Add('Video start produced an H.264/keyframe probe warning') }

        $records.Add([pscustomobject]@{
            MediaType = $mediaType
            Title = $cleanTitle
            Year = $year
            Series = if ($mediaType -eq 'Show') { $cleanTitle } else { $null }
            Season = $season
            Episode = $episode
            Collection = $collection
            FileName = $file.Name
            Extension = $file.Extension.TrimStart('.').ToLowerInvariant()
            SizeGB = [math]::Round($file.Length / 1GB, 3)
            DurationMinutes = if ($probe.format.duration) { [math]::Round([double]$probe.format.duration / 60, 1) } else { $null }
            Width = if ($video.width) { [int]$video.width } else { $null }
            Height = if ($video.height) { [int]$video.height } else { $null }
            VideoCodec = $video.codec_name
            AudioCodec = $audio.codec_name
            AudioChannels = if ($audio.channels) { [int]$audio.channels } else { $null }
            EmbeddedSubtitleTracks = $subtitles.Count
            EmbeddedEnglishTracks = $englishEmbedded.Count
            ExternalSubtitleFiles = $sidecars.Count
            SubtitleStatus = $subtitleStatus
            Issues = ($issues -join '; ')
            LibraryRoot = $library.Root
            RelativePath = $relative
            FullPath = $file.FullName
            Fingerprint = "$($file.Length):$($file.LastWriteTimeUtc.Ticks)"
        })
    }
}

$records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputJson -Encoding UTF8
Write-Output "Wrote $($records.Count) records to $OutputJson"
