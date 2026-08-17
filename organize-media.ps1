[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$locationsPath = Join-Path $PSScriptRoot 'library-locations.json'
$planPath = Join-Path $PSScriptRoot 'rename-plan.csv'
$planJsonPath = Join-Path $PSScriptRoot 'rename-plan.json'
$episodeTitlesPath = Join-Path $PSScriptRoot 'episode-titles.json'
$videoExtensions = @('.mkv','.mp4','.avi','.mov','.m4v','.wmv','.webm','.mpg','.mpeg','.ts','.m2ts')
$roots = if (Test-Path -LiteralPath $locationsPath) {
    Get-Content -LiteralPath $locationsPath -Raw | ConvertFrom-Json
} else {
    @('D:\Movies','D:\Shows','C:\Users\clive\Videos\Movies','C:\Users\clive\Videos\Shows')
}
$roots = @($roots | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { (Resolve-Path -LiteralPath $_).Path.TrimEnd('\') })
$episodeLookup = @{}
if (Test-Path -LiteralPath $episodeTitlesPath) {
    $episodeRecords = Get-Content -LiteralPath $episodeTitlesPath -Raw | ConvertFrom-Json
    foreach ($record in @($episodeRecords)) {
        $key = (([string]$record.Series).ToLowerInvariant() + '|' + [int]$record.Season + '|' + [int]$record.Episode)
        $episodeLookup[$key] = [string]$record.Title
    }
}

function Get-SafeName([string]$Name) {
    foreach ($character in [IO.Path]::GetInvalidFileNameChars()) { $Name = $Name.Replace([string]$character, '') }
    return ($Name -replace '\s{2,}', ' ').Trim(' ','.','-')
}

function Remove-SourcePrefix([string]$Name) {
    $Name = $Name -replace '(?i)^\s*(?:\[(?:www\.)?[^\]]*(?:torrent|\.com|\.net|\.to|movcr|yts|yify|tgx|galaxy|rarbg|y2flix)[^\]]*\]\s*[-–—]?\s*)+', ''
    $Name = $Name -replace '(?i)^\s*(?:www\.)?\S+\.(?:com|net|to|cc)\s*[-–—]?\s*', ''
    return $Name
}

function Remove-ReleaseTail([string]$Name) {
    $releasePattern = '(?i)(?:^|\s|[-–—])(?:2160p|1080p|720p|480p|UHD|HDR10?|DV|WEB(?:-?DL)?|WEBRip|BluRay|BRRip|BDRip|HDRip|DVDRip|REMUX|AMZN|ATVP|NF|HMAX|HULU|x26[45]|h\.?26[45]|HEVC|XviD|AV1|10bit|8bit|AAC|AC3|EAC3|DDP?\d?(?:\.\d)?|DTS(?:-HD)?|TrueHD|Atmos|MP3|GalaxyTV|GalaxyRG\d*|YIFY|YTS|TGx|MovCR|y2flix|AFG|BONE|RARBG|NeoNoir|Tigole)\b.*$'
    $Name = $Name -replace $releasePattern, ''
    $Name = $Name -replace '(?i)\s*\[(?:www\.)?[^\]]*(?:torrent|\.com|\.net|\.to|movcr|yts|yify|tgx|galaxy|rarbg|y2flix)[^\]]*\]\s*', ' '
    return Get-SafeName $Name
}

function Get-Editions([string]$Original) {
    $labels = [System.Collections.Generic.List[string]]::new()
    if ($Original -match '(?i)EXTENDED(?: CUT)?') { $labels.Add('Extended Cut') }
    if ($Original -match '(?i)DIRECTOR.?S CUT|\bDC\b') { $labels.Add("Director's Cut") }
    if ($Original -match '(?i)UNRATED') { $labels.Add('Unrated') }
    if ($Original -match '(?i)REMASTERED') { $labels.Add('Remastered') }
    return @( $labels | Select-Object -Unique )
}

function Find-Episode([string]$Name) {
    $patterns = @(
        '(?i)^(?<series>.*?)\s*S(?<season>\d{1,2})\s*E\s*(?<episode>\d{1,2})(?<tail>.*)$',
        '(?i)^(?<series>.*?)\s*(?<season>\d{1,2})x(?<episode>\d{1,2})(?<tail>.*)$',
        '(?i)^(?<series>.*?)\s*Season\s*(?<season>\d{1,2})\s*(?:Episode|Ep)\s*(?<episode>\d{1,2})(?<tail>.*)$'
    )
    foreach ($pattern in $patterns) {
        $match = [regex]::Match($Name, $pattern)
        if ($match.Success) { return $match }
    }
    return $null
}

function Get-ShowFolderName([string]$Folder) {
    $name = $Folder -replace '[._]+', ' '
    $name = $name -replace '\s{2,}', ' '
    $name = $name -replace '(?i)\s*-\s*The\s+Complete\s+Series.*$', ''
    $name = $name -replace '(?i)\s*\+\s*Extras.*$', ''
    $name = $name -replace '(?i)\s+Complete\s+Series.*$', ''
    $name = $name.Trim(' ','.','-','(',')')
    if ($name -match '(?i)^(Shows|Series)$') { return '' }
    return $name
}

function Test-TorrentInProgress([IO.FileInfo]$File) {
    $directoryName = [string]$File.DirectoryName
    if ($directoryName -match '(?i)(?:\\|^)(?:incomplete|downloading|partial|\.incomplete)(?:\\|$)') { return $true }
    $markers = @(Get-ChildItem -LiteralPath $File.DirectoryName -Force -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)(?:\.aria2|\.!qB|\.part|\.crdownload|\.opdownload|\.torrent$|\.fastresume$)'
    })
    if ($markers.Count -gt 0) { return $true }
    # Informational "Torrent Downloaded From ..." files are common after completion;
    # treat them as a warning only while video files in the same folder are still changing.
    $originMarkers = @()
    $originDirectory = $File.Directory
    while ($null -ne $originDirectory) {
        $originMarkers += @(Get-ChildItem -LiteralPath $originDirectory.FullName -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '(?i)^torrent\s+downloaded\s+from' })
        if ($originMarkers.Count -gt 0) { break }
        $originDirectory = $originDirectory.Parent
    }
    # Keep the entire torrent-origin tree out of automated operations. Remove the
    # origin marker files (or move the finished media to a clean folder) when it is
    # genuinely complete and ready for library organization.
    if ($originMarkers.Count -gt 0) { return $true }
    # A torrent client can keep the final media name while it writes pieces.
    # An exclusive-open probe prevents a rename from interrupting that write.
    try {
        $handle = [IO.File]::Open($File.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $handle.Dispose()
    }
    catch { return $true }
    # Very recent writes are still settling, even if the client briefly released the handle.
    if (((Get-Date) - $File.LastWriteTime).TotalSeconds -lt 90) { return $true }
    return $false
}

function Get-CleanStem([IO.FileInfo]$File) {
    $original = $File.BaseName
    $name = Remove-SourcePrefix $original
    $name = $name -replace '[._]+', ' '
    $name = $name -replace '\s{2,}', ' '

    $episode = Find-Episode $name
    if ($null -ne $episode) {
        $series = Get-SafeName (Remove-SourcePrefix $episode.Groups['series'].Value)
        $series = $series -replace '(?i)^Marvels The Punisher$', "Marvel's The Punisher"
        $series = $series -replace '(?i)^FROM$', 'From'
        $episodeTitle = Get-SafeName (Remove-ReleaseTail ($episode.Groups['tail'].Value.Trim(' ','.','-')))
        if (-not $episodeTitle) {
            $lookupKey = ($series.ToLowerInvariant() + '|' + [int]$episode.Groups['season'].Value + '|' + [int]$episode.Groups['episode'].Value)
            if ($episodeLookup.ContainsKey($lookupKey)) { $episodeTitle = Get-SafeName $episodeLookup[$lookupKey] }
        }
        $code = 'S' + ([int]$episode.Groups['season'].Value).ToString('00') + 'E' + ([int]$episode.Groups['episode'].Value).ToString('00')
        $result = "$series - $code"
        if ($episodeTitle) { $result += " - $episodeTitle" }
        return Get-SafeName $result
    }

    $name = $name -replace '^\d{1,2}\s+', ''
    $yearMatch = [regex]::Match($name, '\b((?:19|20)\d{2})\b')
    if ($yearMatch.Success) {
        $title = Get-SafeName (Remove-ReleaseTail ($name.Substring(0, $yearMatch.Index).Trim(' ','-','(')))
        $title = $title -replace '(?i)\s+Eng$', ''
        if (-not $title) { $title = 'Untitled' }
        $result = "$title ($($yearMatch.Groups[1].Value))"
        $editions = @(Get-Editions $original)
        if ($editions.Count -gt 0) { $result += ' [' + ($editions -join ', ') + ']' }
        return Get-SafeName $result
    }

    return Get-SafeName (Remove-ReleaseTail $name)
}

$records = [System.Collections.Generic.List[object]]::new()
$seenFiles = @{}
$protectedCount = 0
foreach ($root in $roots) {
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -ErrorAction SilentlyContinue) {
        if ($videoExtensions -notcontains $file.Extension.ToLowerInvariant() -or $seenFiles.ContainsKey($file.FullName)) { continue }
        $seenFiles[$file.FullName] = $true
        if (Test-TorrentInProgress $file) { $protectedCount++; continue }
        $records.Add([pscustomobject]@{ Root = $root; File = $file })
    }
}

$reserved = @{}
$plan = [System.Collections.Generic.List[object]]::new()
foreach ($record in $records) {
    $file = $record.File
    $stem = Get-CleanStem $file
    if (-not $stem) { continue }
    # Normalize episodic media into the library layout expected by the app:
    #   Shows\Series Name\Season 01\Series Name - S01E01 - Title.ext
    # Movies intentionally remain in their existing movie folder.
    $episodeMatch = Find-Episode $file.BaseName
    if ($null -ne $episodeMatch) {
        $seriesFolder = Get-SafeName (Remove-SourcePrefix $episodeMatch.Groups['series'].Value.Trim(' ','.','-'))
        $seriesFolder = $seriesFolder -replace '(?i)^Marvels The Punisher$', "Marvel's The Punisher"
        $seriesFolder = $seriesFolder -replace '(?i)^FROM$', 'From'
        $seasonFolder = 'Season ' + ([int]$episodeMatch.Groups['season'].Value).ToString('00')
        $targetDirectory = Join-Path (Join-Path $record.Root $seriesFolder) $seasonFolder
        $target = Join-Path $targetDirectory ($stem + $file.Extension.ToLowerInvariant())
    } else {
        $relativeDirectory = $file.DirectoryName.Substring($record.Root.Length).TrimStart('\','/')
        $firstFolder = ($relativeDirectory -split '[\\/]')[0]
        $showFolder = Get-ShowFolderName $firstFolder
        if ($showFolder -and $file.FullName -match '(?i)extras|bonus') {
            $targetDirectory = Join-Path (Join-Path $record.Root $showFolder) 'Extras'
            $target = Join-Path $targetDirectory ($stem + $file.Extension.ToLowerInvariant())
        } else {
            $target = Join-Path $file.DirectoryName ($stem + $file.Extension.ToLowerInvariant())
        }
    }
    $candidate = $target
    $suffix = 2
    while (($reserved.ContainsKey($candidate) -and $reserved[$candidate] -ne $file.FullName) -or ((Test-Path -LiteralPath $candidate) -and -not $candidate.Equals($file.FullName,[StringComparison]::OrdinalIgnoreCase))) {
        $candidate = Join-Path $file.DirectoryName ("$stem [$suffix]$($file.Extension.ToLowerInvariant())")
        $suffix++
    }
    $reserved[$candidate] = $file.FullName
    if (-not $candidate.Equals($file.FullName,[StringComparison]::Ordinal)) {
        $plan.Add([pscustomobject]@{ OldPath = $file.FullName; NewPath = $candidate })
    }
}

$plan | Export-Csv -LiteralPath $planPath -NoTypeInformation -Encoding UTF8
$plan | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $planJsonPath -Encoding UTF8
if (-not $Apply) {
    [pscustomobject]@{ Scanned = $records.Count; Protected = $protectedCount; Planned = $plan.Count; Plan = $planPath; Applied = $false }
    return
}

$completed = 0
$skipped = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $plan) {
    try { $file = Get-Item -LiteralPath $entry.OldPath -ErrorAction Stop }
    catch { $skipped.Add([pscustomobject]@{ Path = $entry.OldPath; Reason = 'Source file no longer exists' }); continue }
    if (Test-TorrentInProgress $file) {
        $skipped.Add([pscustomobject]@{ Path = $entry.OldPath; Reason = 'Active or incomplete torrent download' })
        continue
    }
    $oldBase = $file.BaseName
    $newBase = [IO.Path]::GetFileNameWithoutExtension($entry.NewPath)
    $sidecars = @(Get-ChildItem -LiteralPath $file.DirectoryName -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Name.StartsWith($oldBase + '.',[StringComparison]::OrdinalIgnoreCase) -and $_.FullName -ne $file.FullName
    })
    $moved = $false
    $lastError = $null
    for ($attempt = 1; $attempt -le 5 -and -not $moved; $attempt++) {
        try {
            $destinationDirectory = Split-Path -Parent $entry.NewPath
            if (-not (Test-Path -LiteralPath $destinationDirectory)) {
                New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            }
            Move-Item -LiteralPath $file.FullName -Destination $entry.NewPath -ErrorAction Stop
            $moved = $true
        }
        catch {
            $lastError = $_.Exception.Message
            if ($attempt -lt 5) { Start-Sleep -Milliseconds 500 }
        }
    }
    if (-not $moved) {
        $skipped.Add([pscustomobject]@{ Path = $entry.OldPath; Reason = $lastError })
        continue
    }
    foreach ($sidecar in $sidecars) {
        $newSidecar = Join-Path (Split-Path -Parent $entry.NewPath) ($newBase + $sidecar.Name.Substring($oldBase.Length))
        if (-not (Test-Path -LiteralPath $newSidecar)) { Move-Item -LiteralPath $sidecar.FullName -Destination $newSidecar }
    }
    $completed++
}
$skippedPath = Join-Path $PSScriptRoot 'rename-skipped.json'
$skipped | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $skippedPath -Encoding UTF8
[pscustomobject]@{ Scanned = $records.Count; Protected = $protectedCount; Planned = $plan.Count; Renamed = $completed; Skipped = $skipped.Count; SkippedReport = $skippedPath; Applied = $true } | ConvertTo-Json -Compress
