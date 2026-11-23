# Project notes

Decisions and rules for NEKCA Apps (the launcher). Keep this up to date.

## Why it works this way

- **Per user, no admin.** NEKCA laptops are locked down, so everything installs under `%LOCALAPPDATA%\NEKCA Apps` and
  shortcuts go in the user's Start Menu (`Programs\NEKCA Apps`) and desktop.
- **Side-by-side versions.** `apps\<id>\<version>\`. Installing never overwrites a version, so an app can stay open while
  a new version installs; switching versions just repoints the shortcuts. `state.json` says which version is in use.
  A version is recorded only once its folder is complete (unpacked into `<version>.partial`, then renamed).
- **Keep two.** After a switch, everything except the version in use and the previous one is removed (a folder that's
  open is skipped and retried at the next start). Any version can be reinstalled from "All versions".
- **Public GitHub, no sign-in.** Versions are GitHub Releases. The release list is read from the API with ETags and
  cached in `cache\`: GitHub allows 60 unsigned requests an hour per network address, but "not modified" answers don't
  count, and the cached list is shown when GitHub can't be reached. Zips are checked against GitHub's SHA-256 when
  GitHub provides one. Downloads made by the launcher don't carry the "from the internet" mark, so Windows
  SmartScreen doesn't stop the apps.
- **The app list is online.** `apps.json` is read from `main` on GitHub at start, so adding an app needs no launcher
  release. The copy built into the launcher (and the last one read) is used when offline.
- **The launcher installs itself like an app** (`apps\nekca-apps\<version>`). Run from a downloaded zip, it offers
  "Install NEKCA Apps", copies itself in, makes the shortcuts and restarts from there. When installed it checks its
  own releases and offers "Update now", which installs the new version, switches, and restarts.
- **Plain WPF**, not WebView2, so the launcher works even when an app's browser engine doesn't.

## Apps

- `fuel-intake`: Nerves82/fuel-intake-app, `FuelIntake-{version}-win-x64.zip`, `FuelIntake.exe`. Its settings live in
  `%LOCALAPPDATA%\FuelIntake` (shared by all versions) and its data in the chosen client files folder.
