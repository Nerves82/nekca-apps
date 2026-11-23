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

## Tray, single instance, dark mode

- **One copy per user** (`Local\NekcaApps.Running` mutex). Starting it again signals the running copy
  (`Local\NekcaApps.Show`) to show its window. Self-update releases the lock before starting the new version.
- **Tray**: closing the window hides it (unless "Keep running when the window is closed" is unticked in the tray
  menu); the first time, a tip says so. It checks for updates at start and every 4 hours and shows a notice for each
  new version once. "Quit NEKCA Apps" in the tray menu really exits.
- **Start when Windows starts**: per-user `HKCU\...\CurrentVersion\Run` value "NEKCA Apps" = `"<exe>" --tray` (starts
  hidden). Only for the installed launcher; the value is rewritten at each start so it follows launcher updates.
- **Theme**: System (Windows' app mode, followed live), Light or Dark, saved in `settings.json`. Colours are the
  brushes in App.xaml, swapped by `Theme.Apply`; always use `DynamicResource` for them. The title bar follows too.
- WinForms is referenced only for the tray icon (`NotifyIcon`); its global usings are removed to avoid name clashes.
- **Download page**: `docs/download/index.html` (GitHub Pages from `main` /docs) sends the browser to the newest
  versioned zip, so the downloaded file name shows the version.

## Uninstalling

- An app: the **Uninstall** button beside Open. Removes every version and its shortcuts; the app's own settings and
  data (e.g. `%LOCALAPPDATA%\FuelIntake`, the client files folder) are never touched. An open version is skipped.
- NEKCA Apps itself: tray menu › **Uninstall NEKCA Apps…** (installed launcher only). Yes = also uninstall every app;
  No = keep the apps and their shortcuts (they just stop getting updates). Removes the launcher's shortcuts and the
  start-with-Windows entry, then quits; a hidden `cmd` waits a few seconds and deletes the launcher's files
  (`Installer.UninstallLauncher` lists them; always inside the install folder; the whole folder when no app is left).
