# Building and publishing High Seas Media

## Local release build

From the project folder, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-release.ps1
```

The script builds the Windows app and Android remote, updates the Android version, copies the APK into the Windows release, and creates a portable Windows ZIP. The ZIP includes `installer\Install-HighSeasMedia.cmd`; a tester can extract it and double-click that file for a per-user install with a Start-menu shortcut. No administrator account is required.

To install and launch the build on the development PC, add `-Install`.

## Connect GitHub once

Create an empty GitHub repository, then run this once from the project folder:

```powershell
git init
git remote add origin https://github.com/<account>/<repository>.git
```

Use SSH instead if that is how GitHub is configured on the machine. Do not put access tokens or passwords in the remote URL or in source files.

## Publish a version

After making and testing changes:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish-github.ps1 -Message "Describe the change"
```

This commits tracked source changes, creates a tag such as `v2026.08.16.2030`, pushes the branch and tag, and creates a GitHub release when the GitHub CLI (`gh`) is installed and authenticated. The release assets are the Android APK and portable Windows ZIP. Without `gh`, the script still pushes the code and tag and prints the exact files to attach manually.

For a source-only checkpoint without rebuilding:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish-github.ps1 -SkipBuild -Message "UI tuning"
```

Every release should be tested locally before publishing. Keep personal library databases, downloaded media, API keys, and diagnostics out of commits; the repository `.gitignore` covers the generated local state.

