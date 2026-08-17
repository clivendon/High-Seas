[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DarkTitleBar {
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
'@

$libraryPath = Join-Path $PSScriptRoot 'media-library.json'
$locationsPath = Join-Path $PSScriptRoot 'library-locations.json'
$playerPath = Join-Path $PSScriptRoot 'play-movie.ps1'
if (-not (Test-Path -LiteralPath $libraryPath)) {
    [System.Windows.Forms.MessageBox]::Show('The media library file is missing.', 'Media Center')
    exit 1
}

$script:cachedLibrary = Get-Content -LiteralPath $libraryPath -Raw | ConvertFrom-Json
$script:cachedLibrary = @($script:cachedLibrary)
$defaultLocations = @(
    'D:\Movies'
    'D:\Shows'
    (Join-Path ([Environment]::GetFolderPath('MyVideos')) 'Movies')
    (Join-Path ([Environment]::GetFolderPath('MyVideos')) 'Shows')
) | Where-Object { Test-Path -LiteralPath $_ }
$script:locations = if (Test-Path -LiteralPath $locationsPath) {
    Get-Content -LiteralPath $locationsPath -Raw | ConvertFrom-Json
} else {
    @($defaultLocations)
}
$script:locations = @($script:locations)
$script:library = @()
$script:subtitleChoice = $null

$form = New-Object System.Windows.Forms.Form
$form.Text = 'My Movies & Shows'
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size(1120, 720)
$form.MinimumSize = New-Object System.Drawing.Size(850, 520)
$form.BackColor = [System.Drawing.Color]::FromArgb(24, 26, 31)
$form.ForeColor = [System.Drawing.Color]::White
$form.Font = New-Object System.Drawing.Font('Segoe UI', 10)

$title = New-Object System.Windows.Forms.Label
$title.Text = 'My Movies && Shows'
$title.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 22)
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point(22, 16)
$form.Controls.Add($title)

$search = New-Object System.Windows.Forms.TextBox
$search.Location = New-Object System.Drawing.Point(25, 66)
$search.Size = New-Object System.Drawing.Size(390, 32)
$search.Anchor = 'Top,Left,Right'
$search.BackColor = [System.Drawing.Color]::FromArgb(44, 47, 55)
$search.ForeColor = [System.Drawing.Color]::White
$search.BorderStyle = 'FixedSingle'
$form.Controls.Add($search)

$typeFilter = New-Object System.Windows.Forms.ComboBox
$typeFilter.DropDownStyle = 'DropDownList'
[void]$typeFilter.Items.AddRange(@('Everything', 'Movies', 'Shows'))
$typeFilter.SelectedIndex = 0
$typeFilter.Location = New-Object System.Drawing.Point(430, 66)
$typeFilter.Size = New-Object System.Drawing.Size(145, 32)
$typeFilter.Anchor = 'Top,Right'
$typeFilter.BackColor = [System.Drawing.Color]::FromArgb(44, 47, 55)
$typeFilter.ForeColor = [System.Drawing.Color]::White
$typeFilter.FlatStyle = 'Flat'
$form.Controls.Add($typeFilter)

$folders = New-Object System.Windows.Forms.Button
$folders.Text = 'Library folders...'
$folders.Location = New-Object System.Drawing.Point(590, 62)
$folders.Size = New-Object System.Drawing.Size(150, 38)
$folders.Anchor = 'Top,Right'
$folders.BackColor = [System.Drawing.Color]::FromArgb(52, 56, 66)
$folders.ForeColor = [System.Drawing.Color]::White
$folders.FlatStyle = 'Flat'
$folders.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(80, 84, 94)
$form.Controls.Add($folders)

$refresh = New-Object System.Windows.Forms.Button
$refresh.Text = 'Refresh'
$refresh.Location = New-Object System.Drawing.Point(750, 62)
$refresh.Size = New-Object System.Drawing.Size(100, 38)
$refresh.Anchor = 'Top,Right'
$refresh.BackColor = [System.Drawing.Color]::FromArgb(52, 56, 66)
$refresh.ForeColor = [System.Drawing.Color]::White
$refresh.FlatStyle = 'Flat'
$refresh.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(80, 84, 94)
$form.Controls.Add($refresh)

$list = New-Object System.Windows.Forms.ListView
$list.View = 'Details'
$list.FullRowSelect = $true
$list.MultiSelect = $false
$list.HideSelection = $false
$list.BackColor = [System.Drawing.Color]::FromArgb(34, 37, 44)
$list.ForeColor = [System.Drawing.Color]::White
$list.BorderStyle = 'FixedSingle'
$list.Location = New-Object System.Drawing.Point(25, 112)
$list.Size = New-Object System.Drawing.Size(1050, 440)
$list.Anchor = 'Top,Bottom,Left,Right'
[void]$list.Columns.Add('Title', 370)
[void]$list.Columns.Add('Type', 80)
[void]$list.Columns.Add('Episode', 115)
[void]$list.Columns.Add('Year', 65)
[void]$list.Columns.Add('Subtitles', 210)
[void]$list.Columns.Add('Quality', 120)
$form.Controls.Add($list)

$monitorLabel = New-Object System.Windows.Forms.Label
$monitorLabel.Text = 'Play on:'
$monitorLabel.AutoSize = $true
$monitorLabel.Location = New-Object System.Drawing.Point(25, 580)
$monitorLabel.Anchor = 'Bottom,Left'
$form.Controls.Add($monitorLabel)

$monitor = New-Object System.Windows.Forms.ComboBox
$monitor.DropDownStyle = 'DropDownList'
$monitor.Location = New-Object System.Drawing.Point(90, 575)
$monitor.Size = New-Object System.Drawing.Size(250, 32)
$monitor.Anchor = 'Bottom,Left'
$monitor.BackColor = [System.Drawing.Color]::FromArgb(44, 47, 55)
$monitor.ForeColor = [System.Drawing.Color]::White
$monitor.FlatStyle = 'Flat'
$screens = @([System.Windows.Forms.Screen]::AllScreens)
for ($i = 0; $i -lt $screens.Count; $i++) {
    $suffix = if ($screens[$i].Primary) { ' (primary)' } else { '' }
    [void]$monitor.Items.Add("Monitor $($i + 1) - $($screens[$i].Bounds.Width)x$($screens[$i].Bounds.Height)$suffix")
}
$monitor.SelectedIndex = [Math]::Min(2, $monitor.Items.Count - 1)
$form.Controls.Add($monitor)

$requireSubs = New-Object System.Windows.Forms.CheckBox
$requireSubs.Text = 'Use English subtitles'
$requireSubs.Checked = $true
$requireSubs.AutoSize = $true
$requireSubs.Location = New-Object System.Drawing.Point(365, 579)
$requireSubs.Anchor = 'Bottom,Left'
$form.Controls.Add($requireSubs)

$addSubtitle = New-Object System.Windows.Forms.Button
$addSubtitle.Text = 'Choose subtitle file...'
$addSubtitle.Location = New-Object System.Drawing.Point(535, 571)
$addSubtitle.Size = New-Object System.Drawing.Size(170, 40)
$addSubtitle.Anchor = 'Bottom,Left'
$addSubtitle.BackColor = [System.Drawing.Color]::FromArgb(52, 56, 66)
$addSubtitle.ForeColor = [System.Drawing.Color]::White
$addSubtitle.FlatStyle = 'Flat'
$addSubtitle.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(80, 84, 94)
$form.Controls.Add($addSubtitle)

$play = New-Object System.Windows.Forms.Button
$play.Text = 'PLAY'
$play.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 12)
$play.BackColor = [System.Drawing.Color]::FromArgb(218, 55, 66)
$play.ForeColor = [System.Drawing.Color]::White
$play.FlatStyle = 'Flat'
$play.FlatAppearance.BorderSize = 0
$play.Location = New-Object System.Drawing.Point(890, 566)
$play.Size = New-Object System.Drawing.Size(185, 50)
$play.Anchor = 'Bottom,Right'
$form.Controls.Add($play)

$status = New-Object System.Windows.Forms.Label
$status.Text = "$($script:library.Count) files ready"
$status.AutoSize = $true
$status.ForeColor = [System.Drawing.Color]::Silver
$status.Location = New-Object System.Drawing.Point(25, 635)
$status.Anchor = 'Bottom,Left'
$form.Controls.Add($status)

function Reload-Library {
    $status.Text = 'Scanning selected library folders...'
    [System.Windows.Forms.Application]::DoEvents()

    $cache = @{}
    foreach ($item in $script:cachedLibrary) {
        if ($item.FullPath) { $cache[[string]$item.FullPath] = $item }
    }

    $found = [System.Collections.Generic.List[object]]::new()
    $seen = @{}
    $extensions = @('.mkv', '.mp4', '.avi', '.mov', '.m4v', '.wmv', '.webm', '.mpg', '.mpeg', '.ts', '.m2ts')
    foreach ($root in @($script:locations)) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -ErrorAction SilentlyContinue) {
            if ($extensions -notcontains $file.Extension.ToLowerInvariant()) { continue }
            if ($seen.ContainsKey($file.FullName)) { continue }
            $seen[$file.FullName] = $true
            if ($cache.ContainsKey($file.FullName)) {
                $found.Add($cache[$file.FullName])
                continue
            }

            $isShow = $file.BaseName -match '(?i)S\d{1,2}E\d{1,2}' -or $root -match '(?i)(shows|tv|series)'
            $season = $null
            $episode = $null
            if ($file.BaseName -match '(?i)S(\d{1,2})E(\d{1,2})') {
                $season = $matches[1]
                $episode = $matches[2]
            }
            $base = Join-Path $file.DirectoryName $file.BaseName
            $hasExternal = @('.srt', '.ass', '.ssa', '.vtt') | Where-Object { Test-Path -LiteralPath ($base + $_) } | Select-Object -First 1
            $displayName = ($file.BaseName -replace '[._]+', ' ') -replace '\s{2,}', ' '
            $found.Add([pscustomobject]@{
                MediaType = if ($isShow) { 'Show' } else { 'Movie' }
                Title = $displayName.Trim()
                Series = if ($isShow) { $file.Directory.Name } else { $null }
                Season = $season
                Episode = $episode
                Year = $null
                Height = $null
                SubtitleStatus = if ($hasExternal) { 'External subtitle' } else { 'Not yet checked' }
                FileName = $file.Name
                FullPath = $file.FullName
                LibraryRoot = [string]$root
            })
        }
    }
    $script:library = @($found | Sort-Object MediaType, Title, Season, Episode, FileName)
    Update-List
    $status.Text = "$($script:library.Count) files loaded from $(@($script:locations).Count) folders"
}

function Show-LibraryFolders {
    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = 'Library folders'
    $dialog.StartPosition = 'CenterParent'
    $dialog.Size = New-Object System.Drawing.Size(680, 440)
    $dialog.MinimumSize = New-Object System.Drawing.Size(560, 360)
    $dialog.BackColor = [System.Drawing.Color]::FromArgb(24, 26, 31)
    $dialog.ForeColor = [System.Drawing.Color]::White
    $dialog.Font = New-Object System.Drawing.Font('Segoe UI', 10)

    $prompt = New-Object System.Windows.Forms.Label
    $prompt.Text = 'Add every folder that contains movies or shows. Subfolders are included automatically.'
    $prompt.AutoSize = $true
    $prompt.Location = New-Object System.Drawing.Point(18, 18)
    $dialog.Controls.Add($prompt)

    $folderList = New-Object System.Windows.Forms.ListBox
    $folderList.Location = New-Object System.Drawing.Point(20, 52)
    $folderList.Size = New-Object System.Drawing.Size(620, 245)
    $folderList.Anchor = 'Top,Bottom,Left,Right'
    $folderList.BackColor = [System.Drawing.Color]::FromArgb(34, 37, 44)
    $folderList.ForeColor = [System.Drawing.Color]::White
    foreach ($location in @($script:locations)) { [void]$folderList.Items.Add([string]$location) }
    $dialog.Controls.Add($folderList)

    $add = New-Object System.Windows.Forms.Button
    $add.Text = 'Add folder...'
    $add.Location = New-Object System.Drawing.Point(20, 320)
    $add.Size = New-Object System.Drawing.Size(125, 40)
    $add.Anchor = 'Bottom,Left'
    $add.BackColor = [System.Drawing.Color]::FromArgb(52, 56, 66)
    $add.ForeColor = [System.Drawing.Color]::White
    $add.FlatStyle = 'Flat'
    $dialog.Controls.Add($add)

    $remove = New-Object System.Windows.Forms.Button
    $remove.Text = 'Remove'
    $remove.Location = New-Object System.Drawing.Point(155, 320)
    $remove.Size = New-Object System.Drawing.Size(100, 40)
    $remove.Anchor = 'Bottom,Left'
    $remove.BackColor = [System.Drawing.Color]::FromArgb(52, 56, 66)
    $remove.ForeColor = [System.Drawing.Color]::White
    $remove.FlatStyle = 'Flat'
    $dialog.Controls.Add($remove)

    $save = New-Object System.Windows.Forms.Button
    $save.Text = 'Save && scan'
    $save.Location = New-Object System.Drawing.Point(500, 320)
    $save.Size = New-Object System.Drawing.Size(140, 40)
    $save.Anchor = 'Bottom,Right'
    $save.BackColor = [System.Drawing.Color]::FromArgb(218, 55, 66)
    $save.ForeColor = [System.Drawing.Color]::White
    $save.FlatStyle = 'Flat'
    $save.FlatAppearance.BorderSize = 0
    $dialog.Controls.Add($save)

    $add.Add_Click({
        $picker = New-Object System.Windows.Forms.FolderBrowserDialog
        $picker.Description = 'Choose a folder containing movies or shows'
        $picker.ShowNewFolderButton = $false
        if ($picker.ShowDialog($dialog) -eq 'OK' -and -not $folderList.Items.Contains($picker.SelectedPath)) {
            [void]$folderList.Items.Add($picker.SelectedPath)
        }
    })
    $remove.Add_Click({
        if ($folderList.SelectedIndex -ge 0) { $folderList.Items.RemoveAt($folderList.SelectedIndex) }
    })
    $save.Add_Click({
        $script:locations = @($folderList.Items | ForEach-Object { [string]$_ })
        ConvertTo-Json -InputObject @($script:locations) | Set-Content -LiteralPath $locationsPath -Encoding UTF8
        $dialog.DialogResult = 'OK'
        $dialog.Close()
    })
    if ($dialog.ShowDialog($form) -eq 'OK') { Reload-Library }
}

function Get-DisplayTitle($item) {
    if ($item.MediaType -eq 'Show' -and $item.Series) { return [string]$item.Series }
    return [string]$item.Title
}

function Update-List {
    $query = $search.Text.Trim()
    $kind = $typeFilter.SelectedItem
    $list.BeginUpdate()
    $list.Items.Clear()
    foreach ($media in $script:library) {
        if ($kind -eq 'Movies' -and $media.MediaType -ne 'Movie') { continue }
        if ($kind -eq 'Shows' -and $media.MediaType -ne 'Show') { continue }
        $haystack = "$($media.Title) $($media.Series) $($media.FileName) $($media.Season) $($media.Episode)"
        if ($query -and $haystack.IndexOf($query, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
        $episode = if ($media.MediaType -eq 'Show' -and $media.Season -and $media.Episode) {
            "Season $($media.Season), Episode $($media.Episode)"
        } else { '' }
        $quality = if ($media.Height) { "$($media.Height)p" } else { '' }
        $row = New-Object System.Windows.Forms.ListViewItem((Get-DisplayTitle $media))
        [void]$row.SubItems.Add([string]$media.MediaType)
        [void]$row.SubItems.Add($episode)
        [void]$row.SubItems.Add([string]$media.Year)
        [void]$row.SubItems.Add([string]$media.SubtitleStatus)
        [void]$row.SubItems.Add($quality)
        $row.Tag = $media
        [void]$list.Items.Add($row)
    }
    $list.EndUpdate()
    $status.Text = "$($list.Items.Count) files shown"
}

function Choose-Subtitle {
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = 'Choose an English subtitle file'
    $dialog.Filter = 'Subtitle files (*.srt;*.ass;*.ssa;*.vtt)|*.srt;*.ass;*.ssa;*.vtt|All files (*.*)|*.*'
    if ($dialog.ShowDialog() -eq 'OK') {
        $script:subtitleChoice = $dialog.FileName
        $status.Text = "Subtitle selected: $([IO.Path]::GetFileName($dialog.FileName))"
    }
}

function Start-SelectedMedia {
    if ($list.SelectedItems.Count -eq 0) {
        $status.Text = 'Choose a movie or episode first.'
        return
    }
    $media = $list.SelectedItems[0].Tag
    $args = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $playerPath, '-Path', [string]$media.FullPath, '-Monitor', [string]($monitor.SelectedIndex + 1))
    if ($script:subtitleChoice) {
        $args += @('-SubtitlePath', $script:subtitleChoice)
    }
    elseif ($requireSubs.Checked -and [string]$media.SubtitleStatus -match '^Missing') {
        Choose-Subtitle
        if (-not $script:subtitleChoice) {
            $status.Text = 'Playback cancelled: this file needs an English subtitle.'
            return
        }
        $args += @('-SubtitlePath', $script:subtitleChoice)
    }
    elseif (-not $requireSubs.Checked) {
        $args += '-AllowNoSubtitles'
    }
    $status.Text = "Starting $(Get-DisplayTitle $media) on monitor $($monitor.SelectedIndex + 1)..."
    Start-Process -FilePath 'powershell.exe' -ArgumentList $args -WindowStyle Hidden
    $script:subtitleChoice = $null
}

$search.Add_TextChanged({ Update-List })
$typeFilter.Add_SelectedIndexChanged({ Update-List })
$folders.Add_Click({ Show-LibraryFolders })
$refresh.Add_Click({ Reload-Library })
$addSubtitle.Add_Click({ Choose-Subtitle })
$play.Add_Click({ Start-SelectedMedia })
$list.Add_DoubleClick({ Start-SelectedMedia })
$form.Add_Shown({
    $dark = 1
    [void][DarkTitleBar]::DwmSetWindowAttribute($form.Handle, 20, [ref]$dark, 4)
    $search.Focus()
})

Reload-Library
[void]$form.ShowDialog()
