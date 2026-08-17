# High Seas Media — User Guide

High Seas Media is a local-first media center for movies and shows you own or are otherwise allowed to store and play. It is designed for a couch, a large collection, multiple displays, and a phone remote.

## First launch

1. Open **High Seas Media** from the Start menu or its desktop shortcut.
2. Open **Manage Library → Library folders…**.
3. Add every movie and show location. Multiple folders and drives are supported.
4. Choose **Update library** and let the scan finish.

The scanner reads filenames and folders, then caches posters, descriptions, years, episode titles, subtitles, and collection matches. Video files are not uploaded to metadata providers.

## Home, Movies, Shows, and Collections

- **Home** shows recently added media, collections, movies, and shows.
- **Movies** shows individual films.
- **Shows** groups a series into seasons and episodes.
- **Collections** groups recognizable franchises such as Star Wars, X-Men, Harry Potter, and Riddick.

Collection names use the current number of films. For example, a collection that grows will change from “X-Men 13 Movie Collection” to “X-Men 14 Movie Collection” automatically.

Use the mouse, arrow keys, or WASD to move the focus. Enter opens the focused item. Left/right moves through cards; at the edge of a section it can move to the neighboring top-level page. Backspace returns.

## Playing media

Open a movie, show, season, or episode and choose:

- **Play on** — the monitor to use.
- **Audio through** — the Windows audio device.
- **Use English subtitles** — automatically loads a matched subtitle when one is available.

The monitor picker currently targets displays attached to the Windows PC. Network casting to a TV or another computer is not part of this build yet; the phone remote can move the High Seas Media window between the PC’s local monitors.

Playback is built into High Seas Media. Space pauses or resumes, Left/Right seeks, Up/Down changes volume, M mutes, C toggles captions, and Escape or Backspace closes playback.

The player also has **PREV** and **NEXT** episode buttons. They move within the current show in season/episode order, and the next episode starts automatically when an episode finishes. The phone remote’s previous/next buttons use the same episode controls.

If the player shows **CC N/A**, that file has no selectable subtitle track loaded. Run the subtitle audit or choose a sidecar subtitle before starting playback.

## Subtitles and library maintenance

Subtitle auditing is manual by default. Use **Manage Library → Check subtitles** when you want to run it. Completed checks are cached so repeated audits skip files that already succeeded unless you explicitly request a retry.

**Update library** is the main maintenance action. It can:

- scan all configured locations;
- clean names and organize shows into show/season folders;
- refresh missing posters, descriptions, years, and episode titles;
- download matched subtitles;
- remove empty leftover folders; and
- find duplicate movies or episodes and ask which copy to keep.

Folders that look like active torrent/incomplete-download locations are protected from renaming, moving, or cleanup. Review duplicate choices before confirming deletion.

## qBittorrent dashboard

The **qBittorrent** page is a local dashboard for an existing qBittorrent Web UI. Open it once, enter the Web UI address and optional credentials, and save. It shows transfer progress and provides pause/resume, recheck, and remove-without-deleting-files actions.

High Seas Media does not bypass provider, copyright, or infringement checks. It only talks to the qBittorrent Web API you configure.

## Phone remote

Open **Phone Remote** on the desktop. On the same Wi-Fi, scan the QR code or open the displayed address in your phone browser. The PIN protects the connection.

The remote can browse the library, move focus, play media, change monitor and audio output, control playback, switch between navigation and trackpad mode, and request the latest APK. Keep it on a trusted private network; do not forward the remote port directly to the public internet.

The current Android APK is included with each release as `High-Seas-Remote-v<version>.apk` and as `High-Seas-Remote-latest.apk`.

## Settings and optional services

Settings can store optional API keys for richer metadata and subtitle matching. Keys are stored locally on this PC. They are never included in the library scan or sent with media files.

## Troubleshooting

- **No media appears:** confirm every location is listed under Library folders, then run Update library.
- **A poster is wrong:** correct the filename/title and run Update library again; cached metadata can then be refreshed.
- **A subtitle is missing:** run Check subtitles, confirm the title/year/season/episode is correct, and retry only that audit.
- **A torrent is changing:** keep it in its active/incomplete folder until qBittorrent reports 100%; the organizer protects common torrent markers automatically.
- **The phone cannot connect:** confirm both devices are on the same private Wi-Fi and allow the app through Windows Firewall when prompted.
