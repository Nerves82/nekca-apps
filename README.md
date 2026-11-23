# NEKCA Apps

One place to install, update and roll back NEKCA's desktop apps. No admin rights needed.

## Getting it

**Newest version**
[![Latest release](https://img.shields.io/github/v/release/Nerves82/nekca-apps?label=latest&cacheSeconds=60)](https://github.com/Nerves82/nekca-apps/releases/latest):
**[Download NEKCA Apps](https://nerves82.github.io/nekca-apps/download/)**
(the file is named with its version, e.g. `NekcaApps-0.1.4-win-x64.zip`)
· [All releases](https://github.com/Nerves82/nekca-apps/releases)

Backup link with a fixed file name (no version in it):
[NekcaApps-win-x64.zip](https://github.com/Nerves82/nekca-apps/releases/latest/download/NekcaApps-win-x64.zip)

Unzip it, run `NekcaApps.exe` and click **Install NEKCA Apps**. Windows may say "unknown publisher" the first time:
choose **More info → Run anyway**. After that, open it from the Start Menu (or the desktop shortcut); it updates itself.

After a merge, the new release takes about 2–3 minutes to build. Until then the links still give the previous version.

## What it does

- Lists the apps in [`apps.json`](apps.json), each with its status: not installed, up to date, or update available.
- **Install** or **Update** downloads the newest release from the app's GitHub repo in one click; **Open** starts it.
- **All versions** lists every release with its changes. Pick any version to go back to it (or forward again).
- Keeps a Start Menu shortcut (and, if you tick it, a desktop shortcut) pointing at the version in use.
- Updates itself: when there's a new NEKCA Apps release, a banner offers **Update now**.
- Stays running by the clock (the system tray) when its window is closed, checks for updates every few hours and
  pops up a notice when one is out. Right-click the tray icon to open it, check now, start it with Windows, or quit.
- Light and dark mode (or follow Windows), from the button at the top.

Everything lives under `%LOCALAPPDATA%\NEKCA Apps` (per user, no admin). Each version gets its own folder, so an update
never breaks a copy that's open. The version in use and the one before it are kept; older ones are removed. Apps keep
their own settings and data elsewhere, so switching versions or uninstalling doesn't touch them.

## Adding an app

1. The app's repo must be **public** and publish GitHub Releases tagged like `v1.2.3`, each with a zip named with the
   version (e.g. `MyApp-1.2.3-win-x64.zip`) that contains the program. The fuel app's `build.yml` does exactly this.
2. Add it to `apps.json`:

   ```json
   {
     "id": "my-app",
     "name": "My App",
     "description": "One line about what it's for.",
     "repo": "Nerves82/my-app",
     "asset": "MyApp-{version}-win-x64.zip",
     "exe": "MyApp.exe"
   }
   ```

   `id` is the install folder name: lower-case letters, digits and dashes, and never change it later.
3. Merge to `main`. Launchers read `apps.json` from GitHub when they start, so the app shows up without a launcher update.

## Development

- `src/NekcaApps.Core`: the app list, GitHub releases, versions, installing and switching. No Windows-only code; tested
  in `tests/NekcaApps.Core.Tests`.
- `src/NekcaApps`: the WPF window, shortcuts and self-update.
- CI (GitHub Actions, Windows) builds and tests every push; every push to `main` publishes a release `v0.1.<run>`.
