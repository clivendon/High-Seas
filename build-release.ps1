param(
    [switch]$SkipAndroid,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$dotnetHome = Join-Path $projectRoot '.dotnet-home'
$androidProject = Join-Path $projectRoot 'android-remote\CliveRemote.csproj'
$windowsProject = Join-Path $projectRoot 'src\CliveMediaCenter\CliveMediaCenter.csproj'
$publishFolder = Join-Path $projectRoot 'publish'
$phoneApk = Join-Path $projectRoot 'phone-app\High-Seas-Remote-latest.apk'
$phoneVersion = Join-Path $projectRoot 'phone-app\High-Seas-Remote-version.txt'
$desktopIcon = Join-Path $projectRoot 'src\CliveMediaCenter\high-seas-taskbar.ico'
$installerSource = Join-Path $projectRoot 'installer'
$androidSdk = Join-Path $projectRoot '.android-sdk'
$jdkRoot = Get-ChildItem -LiteralPath (Join-Path $projectRoot '.android-jdk-extract') -Directory | Select-Object -First 1
$env:DOTNET_CLI_HOME = $dotnetHome

if (-not $SkipAndroid)
{
    if ($null -eq $jdkRoot -or -not (Test-Path -LiteralPath $androidSdk)) { throw 'Android build tools are missing.' }
    $minutesSince2025 = [int](([datetime]::UtcNow - [datetime]'2025-01-01T00:00:00Z').TotalMinutes)
    $displayVersion = [datetime]::Now.ToString('yyyy.MM.dd.HHmm')
    dotnet publish $androidProject -c Release -f net9.0-android `
        -p:AndroidSdkDirectory="$androidSdk" `
        -p:JavaSdkDirectory="$($jdkRoot.FullName)" `
        -p:AndroidPackageFormat=apk `
        -p:ApplicationVersion=$minutesSince2025 `
        -p:ApplicationDisplayVersion=$displayVersion
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
    $signedApk = Join-Path $projectRoot 'android-remote\bin\Release\net9.0-android\com.clivemedia.remote-Signed.apk'
    Copy-Item -LiteralPath $signedApk -Destination $phoneApk -Force
    Set-Content -LiteralPath $phoneVersion -Value $displayVersion -Encoding ascii
    Copy-Item -LiteralPath $signedApk -Destination (Join-Path $projectRoot "High-Seas-Remote-v$displayVersion.apk") -Force
}

dotnet publish $windowsProject -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $publishFolder
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }

# The phone app is a downloadable sidecar served by the desktop remote. Single-file publish does
# not reliably copy linked APK content, so place the signed package explicitly in every release.
Copy-Item -LiteralPath $phoneApk -Destination (Join-Path $publishFolder 'High-Seas-Remote-latest.apk') -Force
Copy-Item -LiteralPath $phoneVersion -Destination (Join-Path $publishFolder 'High-Seas-Remote-version.txt') -Force
Copy-Item -LiteralPath $desktopIcon -Destination (Join-Path $publishFolder 'high-seas-taskbar.ico') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'install-release.ps1') -Destination (Join-Path $publishFolder 'install-release.ps1') -Force
if (Test-Path -LiteralPath $installerSource)
{
    New-Item -ItemType Directory -Path (Join-Path $publishFolder 'installer') -Force | Out-Null
    Copy-Item -Path (Join-Path $installerSource '*') -Destination (Join-Path $publishFolder 'installer') -Recurse -Force
}
# PublishSingleFile does not reliably carry arbitrary nested tool content through
# the publish graph. Copy the media utilities explicitly so subtitle auditing,
# synchronization, and thumbnail generation work from the installed app too.
$sourceTools = Join-Path $projectRoot 'tools'
$publishedTools = Join-Path $publishFolder 'tools'
if (Test-Path -LiteralPath $sourceTools)
{
    New-Item -ItemType Directory -Path $publishedTools -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceTools '*') -Destination $publishedTools -Recurse -Force
}

$releaseVersion = (Get-Content -LiteralPath $phoneVersion -Raw).Trim()
$portableZip = Join-Path $projectRoot "High-Seas-Media-v$releaseVersion-Windows.zip"
Compress-Archive -Path (Join-Path $publishFolder '*') -DestinationPath $portableZip -Force

if ($Install)
{
    & (Join-Path $projectRoot 'install-release.ps1') -PublishFolder $publishFolder -Launch
}

[pscustomobject]@{
    WindowsApp = Join-Path $publishFolder 'CliveMediaCenter.exe'
    AndroidApp = $phoneApk
    WindowsZip = $portableZip
}
