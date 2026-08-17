param(
    [string] $PublishFolder = (Join-Path $PSScriptRoot 'publish'),
    [switch] $Launch
)

$ErrorActionPreference = 'Stop'
$sourceExecutable = Join-Path $PublishFolder 'CliveMediaCenter.exe'
$sourceApk = Join-Path $PublishFolder 'High-Seas-Remote-latest.apk'
$installFolder = Join-Path $env:LOCALAPPDATA 'Programs\High Seas Media'
$installedExecutable = Join-Path $installFolder 'HighSeasMedia.exe'
$installedIcon = Join-Path $installFolder 'high-seas-taskbar.ico'
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\High Seas Media.lnk'

if (-not (Test-Path -LiteralPath $sourceExecutable)) { throw "Desktop release not found: $sourceExecutable" }
if (-not (Test-Path -LiteralPath $sourceApk)) { throw "Android release not found: $sourceApk" }

# Close only this app so its executable can be updated without disturbing VLC or other players.
Get-Process -Name 'CliveMediaCenter', 'HighSeasMedia' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
New-Item -ItemType Directory -Path $installFolder -Force | Out-Null
Copy-Item -Path (Join-Path $PublishFolder '*') -Destination $installFolder -Recurse -Force
Copy-Item -LiteralPath $sourceExecutable -Destination $installedExecutable -Force
if (-not (Test-Path -LiteralPath $installedIcon)) { throw "High Seas icon asset missing: $installedIcon" }

# A standard per-user shortcut makes the app appear under H in Start > All apps. Windows requires
# the user to choose Pin to Start themselves, but the installed entry and icon are fully managed.
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($startMenuShortcut)
$shortcut.TargetPath = $installedExecutable
$shortcut.WorkingDirectory = $installFolder
$shortcut.IconLocation = "$installedIcon,0"
$shortcut.Description = 'High Seas Media - your personal movie and television treasure chest'
$shortcut.Save()

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HighSeasInstallerShellRefresh {
    [DllImport("shell32.dll")] public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
'@
[HighSeasInstallerShellRefresh]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

if ($Launch) { Start-Process -FilePath $installedExecutable }

[pscustomobject]@{
    DesktopApp = $installedExecutable
    AndroidApp = Join-Path $installFolder 'High-Seas-Remote-latest.apk'
    StartMenuShortcut = $startMenuShortcut
}
