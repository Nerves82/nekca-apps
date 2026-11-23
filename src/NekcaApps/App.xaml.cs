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

    private Mutex? single;
    private EventWaitHandle? showSignal;
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

        // already running? ask it to show its window, then leave
        single = new Mutex(true, @"Local\NekcaApps.Running", out var first);
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NekcaApps.Show");
        if (!first)
        {
            showSignal.Set();
            single.Dispose(); single = null;
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true, Name = "show-window" };
        listener.Start();

        Settings = LauncherSettings.Load(Layout);
        Theme.Apply(Settings.Theme);
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (Settings.Theme == AppTheme.System) Theme.Apply(Settings.Theme); });
        CreateTray();
        SyncStartWithWindows();

        window = new MainWindow();
        var startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (!startHidden) window.Show();
        window.StartChecking(CheckEvery);
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
        ReleaseForRestart();
        base.OnExit(e);
    }
}
