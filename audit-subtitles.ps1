[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$libraryPath = Join-Path $PSScriptRoot 'media-library.json'
$reportPath = Join-Path $PSScriptRoot 'subtitle-audit.csv'
$player = Join-Path $PSScriptRoot 'play-movie.ps1'
$ffprobe = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tools\ffmpeg') -Filter ffprobe.exe -File -Recurse | Select-Object -First 1 -ExpandProperty FullName
$media = Get-Content -LiteralPath $libraryPath -Raw | ConvertFrom-Json
$media = @($media)
$report = [System.Collections.Generic.List[object]]::new()

foreach ($item in $media) {
    if (-not $item.FullPath -or -not (Test-Path -LiteralPath $item.FullPath)) { continue }
    $file = Get-Item -LiteralPath $item.FullPath
    $base = Join-Path $file.DirectoryName $file.BaseName
    $active = "$base.srt"
    $source = "$base.source.srt"
    $marker = "$base.subtitle-sync.json"
    $status = 'Missing English subtitle'
    $action = 'Needs subtitle download'

    if (Test-Path -LiteralPath $marker) {
        $status = 'Audio-synced external'
        $action = 'None'
    }
    elseif ((Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $active)) {
        try {
            & $player -Path $file.FullName -NoLaunch | Out-Null
            $status = 'Audio-synced external'
            $action = 'Synchronized now'
        } catch {
            $status = 'External subtitle found'
            $action = 'Sync needs review'
        }
    }
    else {
        $probe = (& $ffprobe -v error -show_entries 'stream=codec_type:stream_tags=language,title' -of json -- $file.FullName | Out-String) | ConvertFrom-Json
        $english = @($probe.streams | Where-Object { $_.codec_type -eq 'subtitle' -and ($_.tags.language -match '^(eng|en)$' -or $_.tags.title -match 'English') })
        if ($english.Count -gt 0) {
            $status = 'Embedded English'
            $action = 'None'
        }
    }
    $item.SubtitleStatus = $status
    $report.Add([pscustomobject]@{ Title = $item.Title; File = $file.FullName; Status = $status; Action = $action })
}

$report | Export-Csv -LiteralPath $reportPath -NoTypeInformation -Encoding UTF8
$media | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $libraryPath -Encoding UTF8
[pscustomobject]@{
    Checked = $report.Count
    Ready = @($report | Where-Object Action -eq 'None').Count
    Synchronized = @($report | Where-Object Action -eq 'Synchronized now').Count
    MissingDownload = @($report | Where-Object Action -eq 'Needs subtitle download').Count
    Review = @($report | Where-Object Action -eq 'Sync needs review').Count
    Report = $reportPath
}
