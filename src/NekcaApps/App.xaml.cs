using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using NekcaApps.Core;
using Forms = System.Windows.Forms;

namespace NekcaApps;

/// <summary>
/// One NEKCA Apps per user: a second start just brings the first one's window up. Closing the window keeps it
/// running by the clock (the tray), checking for updates; "Quit" in the tray menu ends it.
/// </summary>
public partial class App : Application
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "NEKCA Apps";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(4);

    private const string RunningName = @"Local\NekcaApps.Running";
    private Mutex? single;
    private Mutex? setupLock;
    private EventWaitHandle? showSignal;
    private EventWaitHandle? quitSignal;
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripMenuItem? startWithWindows;
    private MainWindow? window;

    public InstallLayout Layout { get; } = InstallLayout.ForCurrentUser();
    public LauncherSettings Settings { get; private set; } = new();
    public bool Quitting { get; private set; }

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Something went wrong: " + args.Exception.Message, "NEKCA Apps", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);

        // run as the setup download (NekcaAppsSetup.exe)? install or update, start the installed copy, and leave
        var running = NekcaApps.MainWindow.RunningVersion();
        var runsInstalled = LauncherRules.RunsInstalled(Layout, AppContext.BaseDirectory);
        // one setup at a time: a second click on the setup file waits for the first, then finds it installed
        // and just opens it (before, both copied at once and the second hit the program the first had started)
        if (!runsInstalled && SetupRules.IsSetupFile(Environment.ProcessPath))
        {
            setupLock = new Mutex(false, @"Local\NekcaApps.Setup");
            try { setupLock.WaitOne(TimeSpan.FromMinutes(2)); } catch (AbandonedMutexException) { }
        }
        var state = InstallState.Load(Layout);
        state.Reconcile(Layout);
        var installedActive = state.For(Launcher.Entry.Id).Active;
        switch (SetupRules.Decide(Environment.ProcessPath, runsInstalled, running, installedActive))
        {
            case SetupAction.Install:
                RunSetup(state, running);
                return;
            case SetupAction.OpenInstalled:
                var exe = Path.Combine(Layout.VersionFolder(Launcher.Entry.Id, installedActive!), state.For(Launcher.Entry.Id).Installed[installedActive!]);
                Start(exe, "");
                Shutdown();
                return;
        }

        // already running? ask it to show its window, then leave (or, to uninstall, ask it to quit first)
        var uninstalling = e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);
        single = new Mutex(true, RunningName, out var first);
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NekcaApps.Show");
        quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NekcaApps.Quit");
        if (!first && !(uninstalling && TakeOver(single, quitSignal)))
        {
            if (uninstalling)
                MessageBox.Show("NEKCA Apps is running and didn't close. Quit it from its icon by the clock, then try again.", "NEKCA Apps");
            else showSignal.Set();
            single.Dispose(); single = null;
            Shutdown();
            return;
        }
        new Thread(() => { while (showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowWindow); }) { IsBackground = true, Name = "show-window" }.Start();
        new Thread(() => { if (quitSignal.WaitOne()) Dispatcher.BeginInvoke(Quit); }) { IsBackground = true, Name = "quit-window" }.Start();
        if (runsInstalled) RegisterInstalledApp(running);

        Settings = LauncherSettings.Load(Layout);
        Theme.Apply(Settings.Theme);
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (Settings.Theme == AppTheme.System) Theme.Apply(Settings.Theme); });
        CreateTray();
        SyncStartWithWindows();

        window = new MainWindow();
        if (uninstalling)
        {
            _ = UninstallLauncherAsync(fromWindowsSettings: true);
            return;
        }
        if (e.Args.Contains("--installed", StringComparer.OrdinalIgnoreCase))
            window.ShowWelcome("NEKCA Apps is installed. Open it any time from the Start Menu or the desktop shortcut. " +
                               "Closing this window keeps it running by the clock, checking for updates.");
        if (e.Args.Contains("--updated", StringComparer.OrdinalIgnoreCase))
            window.ShowWelcome($"NEKCA Apps was updated to version {running}.");
        var startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (!startHidden) window.Show();
        window.StartChecking(CheckEvery);
    }

    // ---- setup

    /// <summary>
    /// The setup download: copy this program into the install folder, make the shortcuts, close an older copy
    /// that's running, and start the installed one.
    /// </summary>
    private void RunSetup(InstallState state, AppVersion running)
    {
        var installer = new Installer(Layout, state, NekcaApps.MainWindow.Http, new WindowsShortcuts());
        var firstInstall = state.For(Launcher.Entry.Id).Active is null;
        try
        {
            InstallSelf(installer, running);
            if (firstInstall) state.For(Launcher.Entry.Id).DesktopShortcut = true;
            installer.Activate(Launcher.Entry, running.ToString());
        }
        catch (Exception ex) when (ex is InstallException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show("NEKCA Apps couldn't be installed: " + ex.Message, "NEKCA Apps", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        // an older copy running by the clock: ask it to quit so the new one can start
        using (var other = new Mutex(false, RunningName))
        using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NekcaApps.Quit"))
        {
            if (!TakeOver(other, quit))
            {
                MessageBox.Show($"NEKCA Apps {running} is installed. The older copy that's running didn't close: " +
                                "right-click its icon by the clock, choose Quit NEKCA Apps, then open NEKCA Apps from the Start Menu.",
                    "NEKCA Apps", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            other.ReleaseMutex();
        }
        Start(installer.ExePath(Launcher.Entry.Id, running.ToString())!, firstInstall ? "--installed" : "--updated");
        Shutdown();
    }

    /// <summary>Installs the running program: just the one file when it's a single-file build, else its folder.</summary>
    public static void InstallSelf(Installer installer, AppVersion running)
    {
#pragma warning disable IL3000 // empty Location is exactly how a single-file build is recognised
        var singleFile = string.IsNullOrEmpty(typeof(App).Assembly.Location);
#pragma warning restore IL3000
        if (singleFile) installer.InstallFromFile(Launcher.Entry, running.ToString(), Environment.ProcessPath!);
        else installer.InstallFromFolder(Launcher.Entry, running.ToString(), AppContext.BaseDirectory.TrimEnd('\\', '/'));
    }

    /// <summary>
    /// Takes the one-copy lock from a running copy by asking it to quit (newer copies listen; very old ones don't).
    /// True once this process holds the lock.
    /// </summary>
    private static bool TakeOver(Mutex runningLock, EventWaitHandle quit)
    {
        try
        {
            if (runningLock.WaitOne(0)) return true;
            quit.Set();
            return runningLock.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException) { return true; } // it exited without letting go: the lock is ours now
    }

    private static void Start(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });

    /// <summary>
    /// Lists NEKCA Apps in Windows' Settings › Apps › Installed apps (per user, no admin), pointing at this copy;
    /// rewritten at every start because each launcher version lives in its own folder.
    /// </summary>
    private void RegisterInstalledApp(AppVersion running)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            long kb = 0;
            try { kb = new DirectoryInfo(AppContext.BaseDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            using var key = Registry.CurrentUser.CreateSubKey(SetupRules.InstalledAppsKey);
            foreach (var (name, value) in SetupRules.InstalledAppsEntry(exe, running, Layout.Root, kb))
                key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    public void SetTheme(AppTheme mode)
    {
        Settings.Theme = mode;
        Settings.Save(Layout);
        Theme.Apply(mode);
    }

    public void ShowWindow()
    {
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true; window.Topmost = false; // bring to front over other apps
    }

    /// <summary>The window was closed: stay in the tray (tell the person once), or quit if they turned that off.</summary>
    public void WindowClosedToTray()
    {
        if (Settings.TrayTipShown || tray is null) return;
        tray.ShowBalloonTip(6000, "NEKCA Apps is still running",
            "It keeps checking for updates down here by the clock. Right-click this icon to open it or quit.", Forms.ToolTipIcon.Info);
        Settings.TrayTipShown = true;
        Settings.Save(Layout);
    }

    /// <summary>Tells the person about updates found by a background check (each one once).</summary>
    public void AnnounceUpdates(IReadOnlyList<string> updates)
    {
        if (tray is null || updates.Count == 0) return;
        tray.Text = "NEKCA Apps: update available";
        tray.ShowBalloonTip(8000, updates.Count == 1 ? "Update available" : "Updates available",
            string.Join("\n", updates) + "\nClick to open NEKCA Apps.", Forms.ToolTipIcon.Info);
    }

    public void ClearUpdateFlag() { if (tray is not null) tray.Text = "NEKCA Apps"; }

    public void Quit()
    {
        Quitting = true;
        ReleaseForRestart();
        Shutdown();
    }

    /// <summary>Lets go of the tray icon and the one-instance lock, so a new version can start before this one ends.</summary>
    public void ReleaseForRestart()
    {
        if (tray is not null) { tray.Visible = false; tray.Dispose(); tray = null; }
        single?.ReleaseMutex(); single?.Dispose(); single = null;
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open NEKCA Apps", null, (_, _) => ShowWindow());
        menu.Items.Add("Check for updates now", null, async (_, _) => { if (window is not null) await window.CheckInBackgroundAsync(announce: true); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        var closeToTray = new Forms.ToolStripMenuItem("Keep running when the window is closed") { Checked = Settings.CloseToTray, CheckOnClick = true };
        closeToTray.CheckedChanged += (_, _) => { Settings.CloseToTray = closeToTray.Checked; Settings.Save(Layout); };
        menu.Items.Add(closeToTray);
        startWithWindows = new Forms.ToolStripMenuItem("Start when Windows starts") { Checked = Settings.StartWithWindows, CheckOnClick = true };
        startWithWindows.CheckedChanged += (_, _) => { Settings.StartWithWindows = startWithWindows.Checked; Settings.Save(Layout); SyncStartWithWindows(); };
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var uninstall = new Forms.ToolStripMenuItem("Uninstall NEKCA Apps…");
        uninstall.Click += async (_, _) => await UninstallLauncherAsync(fromWindowsSettings: false);
        uninstall.Enabled = LauncherRules.RunsInstalled(Layout, AppContext.BaseDirectory);
        if (!uninstall.Enabled) uninstall.ToolTipText = "NEKCA Apps isn't installed on this computer (it's running from a download).";
        menu.Items.Add(uninstall);
        menu.Items.Add("Quit NEKCA Apps", null, (_, _) => Quit());

        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "NEKCA Apps",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };
        tray.BalloonTipClicked += (_, _) => ShowWindow();
    }

    /// <summary>
    /// Removes NEKCA Apps from this computer: asks whether the apps it installed go too, removes shortcuts and the
    /// start-with-Windows entry, then quits and deletes its own files once it has exited.
    /// </summary>
    private async Task UninstallLauncherAsync(bool fromWindowsSettings)
    {
        if (!fromWindowsSettings) ShowWindow();
        MessageBoxResult Say(string text, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult def = MessageBoxResult.None) =>
            window is { IsVisible: true } w ? MessageBox.Show(w, text, title, buttons, image, def) : MessageBox.Show(text, title, buttons, image, def);

        var answer = Say(
            "Uninstall NEKCA Apps from this computer?\n\n" +
            "Yes: also uninstall every app it installed (their own settings and data stay).\n" +
            "No: keep the apps and their shortcuts; they just won't get updates any more.\n" +
            "Cancel: don't uninstall anything.",
            "Uninstall NEKCA Apps", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Cancel)
        {
            if (fromWindowsSettings) Quit();
            return;
        }

        // every app it may have installed: the list on screen plus the app list (online, saved or built in)
        var catalog = (await new CatalogSource(NekcaApps.MainWindow.Http, Layout.CacheFolder).GetAsync()).Catalog.Apps;
        var apps = window!.KnownApps.Concat(catalog).DistinctBy(a => a.Id).ToList();
        LauncherRemoval removal;
        try { removal = window.Installer.UninstallLauncher(apps, removeApps: answer == MessageBoxResult.Yes); }
        catch (Exception e) when (e is InstallException or IOException or UnauthorizedAccessException)
        {
            Say("NEKCA Apps couldn't be uninstalled: " + e.Message, "NEKCA Apps", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (fromWindowsSettings) Quit();
            return;
        }
        Settings.StartWithWindows = false;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) key.DeleteValue(RunName, throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(SetupRules.InstalledAppsKey, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }

        var note = removal.AppsStillOpen.Count == 0 ? "" :
            $"\n\n{string.Join(", ", removal.AppsStillOpen)} {(removal.AppsStillOpen.Count == 1 ? "was" : "were")} open, so " +
            $"{(removal.AppsStillOpen.Count == 1 ? "its" : "their")} files stayed in {Layout.Root}. Close {(removal.AppsStillOpen.Count == 1 ? "it" : "them")} and delete that folder.";
        Say("NEKCA Apps has been uninstalled." + note, "NEKCA Apps", MessageBoxButton.OK, MessageBoxImage.Information);
        DeleteAfterExit(removal.DeleteAfterExit);
        Quit();
    }

    /// <summary>A hidden command window waits a few seconds for this program to end, then deletes what's listed.</summary>
    private void DeleteAfterExit(IReadOnlyList<string> paths)
    {
        var root = Path.GetFullPath(Layout.Root).TrimEnd('\\');
        var steps = paths.Select(Path.GetFullPath)
            .Where(p => p.Equals(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) // never outside the install folder
            .Select(p => $"(if exist \"{p}\\*\" (rmdir /s /q \"{p}\") else (if exist \"{p}\" del /f /q \"{p}\"))");
        var script = "ping 127.0.0.1 -n 4 >nul & " + string.Join(" & ", steps);
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + script)
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
            });
        }
        catch (Exception) { /* the files stay; harmless */ }
    }

    /// <summary>
    /// Keeps the per-user "run at sign-in" entry pointing at this copy (it moves with every launcher update).
    /// Only for the installed launcher: a copy run from a download would vanish.
    /// </summary>
    private void SyncStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            var installed = LauncherRules.RunsInstalled(Layout, AppContext.BaseDirectory);
            if (Settings.StartWithWindows && installed) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            else if (installed || !Settings.StartWithWindows) key.DeleteValue(RunName, throwOnMissingValue: false);
            if (startWithWindows is not null) startWithWindows.Enabled = installed;
            if (startWithWindows is not null && !installed) startWithWindows.ToolTipText = "Install NEKCA Apps first (the banner in its window).";
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { setupLock?.ReleaseMutex(); } catch (ApplicationException) { } // not held (timed out)
        setupLock?.Dispose();
        ReleaseForRestart();
        base.OnExit(e);
    }
}
