# High Seas Media

High Seas Media is a Windows media center for a personal movie and TV collection. It scans local folders, keeps the library organized, and gives you a simple poster-based interface for watching from the couch.

It is intended for media you own or are otherwise allowed to store and play. It does not acquire movies or shows.

## Features

- Movie and TV library across multiple folders and drives
- Poster browser with movies, shows, seasons, episodes, and collections
- Automatic collection grouping with live film counts (for example, “X-Men 14 Movie Collection”)
- Cached posters, descriptions, years, episode titles, and metadata
- Built-in playback with fullscreen, subtitles, seeking, volume, and resume-friendly controls
- Per-playback monitor selection for multi-monitor setups
- Windows audio-device selection
- Manual subtitle audit with cached results and matched subtitle downloads
- Filename cleanup and show/season organization
- Empty-folder cleanup and duplicate review with a keep/delete prompt
- Protection for active torrent and incomplete-download folders
- Local qBittorrent Web API dashboard with transfer status and safe management actions
- PIN-protected phone remote for browsing, playback, monitor/audio switching, and trackpad mode
- Android remote APK included with every release
- Dark emerald “High Seas” theme

Network casting is planned separately from local monitor output; the current release only targets displays attached to the Windows PC.

## Quick start

1. Download the Windows ZIP from the latest GitHub release.
2. Extract it to a folder you control.
3. Double-click `installer\Install-HighSeasMedia.cmd`.
4. Launch High Seas Media from Start → All apps.
5. Open **Manage Library → Library folders…**, add your movie/show folders, and choose **Update library**.

The installer is per-user and does not require administrator access. It creates the Start-menu shortcut and keeps the Android APK beside the desktop app.

For the full walkthrough, see [USER_GUIDE.md](USER_GUIDE.md).

## Controls

In the library, use the mouse, arrow keys, or WASD. Enter opens the focused item, Backspace goes back, Space controls playback, and F/F11 toggles application fullscreen.

During playback, Space pauses/resumes, Left/Right seeks, Up/Down changes volume, M mutes, C toggles captions, and Escape or Backspace closes playback.

## Building

Requirements:

- Windows 10/11 x64
- .NET 9 SDK
- Visual Studio Build Tools or the .NET Windows Desktop workload
- Android SDK/JDK only when rebuilding the phone remote

Build the Windows app and Android APK with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-release.ps1
```

Add `-Install` to install and launch the Windows build on the development PC. The build creates a portable Windows ZIP containing the one-click installer and a versioned Android APK.

Publishing instructions are in [RELEASE_WORKFLOW.md](RELEASE_WORKFLOW.md). The normal release command is:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish-github.ps1 -Message "Describe the change"
```

## Project layout

- `src/CliveMediaCenter/Program.cs` — library browser, metadata, settings, remote server, and orchestration
- `src/CliveMediaCenter/HighSeasPlayerForm.cs` — built-in player
- `src/CliveMediaCenter/OpenSubtitlesClient.cs` — subtitle provider integration
- `android-remote/` — Android phone remote
- `installer/` — one-click Windows installer wrapper
- `tools/` — local media and thumbnail utilities

## Privacy and safety

Scanning and playback stay on the PC. Metadata services receive parsed titles and years, not video files. API keys, PINs, library paths, and cached data stay in the local Windows profile.

The phone remote is for a trusted private network. Do not forward its HTTP port directly to the public internet. If remote access outside the home is ever added, use an encrypted private network and explicit authentication.

## Third-party software

High Seas Media uses LibVLCSharp/libVLC, FFmpeg components, QRCoder, AudioSwitcher, and other third-party packages. Check their licenses before redistributing a build. A public release should include a generated third-party notices file.
