using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using NekcaApps.Core;

namespace NekcaApps;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly InstallLayout layout = App.Current.Layout;
    private readonly InstallState state;
    private readonly Installer installer;
    private readonly GitHubReleaseSource releases;
    private readonly List<AppCard> cards = [];
    private readonly AppVersion running = RunningVersion();
    private Release? launcherUpdate;
    private readonly UpdateNotices notices = new();
    private System.Windows.Threading.DispatcherTimer? timer;
    private bool loading;

    public MainWindow()
    {
        InitializeComponent();
        state = InstallState.Load(layout);
        state.Reconcile(layout);
        installer = new Installer(layout, state, Http, new WindowsShortcuts());
        releases = new GitHubReleaseSource(Http, layout.CacheFolder);
        VersionText.Text = $"Version {running}";
        InstallBanner.Visibility = LauncherRules.RunsInstalled(layout, AppContext.BaseDirectory) ? Visibility.Collapsed : Visibility.Visible;
        ShowThemeButton();
        SourceInitialized += (_, _) => Theme.TitleBar(this);
        Closing += OnClosing;
    }

    // ---- staying in the tray

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.Current.Quitting) return;
        if (App.Current.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            App.Current.WindowClosedToTray();
        }
        else App.Current.Quit();
    }

    /// <summary>Checks now and then every <paramref name="every"/>, announcing new updates from the tray.</summary>
    public void StartChecking(TimeSpan every)
    {
        timer = new System.Windows.Threading.DispatcherTimer { Interval = every };
        timer.Tick += async (_, _) => await CheckInBackgroundAsync(announce: true);
        timer.Start();
        _ = CheckInBackgroundAsync(announce: !IsVisible);
    }

    public async Task CheckInBackgroundAsync(bool announce)
    {
        await LoadAsync();
        var available = cards.Where(c => c.UpdateAvailable && c.Latest is not null).Select(c => (c.Name, c.Latest!.Version)).ToList();
        if (launcherUpdate is { } u) available.Add(("NEKCA Apps", u.Version));
        var fresh = notices.New(available);
        if (announce && !IsVisible) App.Current.AnnounceUpdates(fresh);
        if (available.Count == 0) App.Current.ClearUpdateFlag();
    }

    // ---- theme

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        App.Current.SetTheme(LauncherSettings.Next(App.Current.Settings.Theme));
        ShowThemeButton();
    }

    private void ShowThemeButton() => ThemeButton.Content = App.Current.Settings.Theme switch
    {
        AppTheme.Light => "☀ Light",
        AppTheme.Dark => "☾ Dark",
        _ => "◐ Auto",
    };

    private async void OnLoaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (loading) return;
        loading = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var (catalog, note) = await new CatalogSource(Http, layout.CacheFolder).GetAsync();
            NoteText.Text = note ?? "";
            NoteText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;

            // keep the cards that are already on screen (so an install in progress isn't lost)
            foreach (var app in catalog.Apps.Where(a => cards.All(c => c.App.Id != a.Id)))
                cards.Add(NewCard(app));
            cards.RemoveAll(c => !c.Busy && catalog.Apps.All(a => a.Id != c.App.Id));
            AppList.ItemsSource = null;
            AppList.ItemsSource = cards;
            LoadingText.Visibility = Visibility.Collapsed;

            var tasks = cards.Select(async c =>
            {
                var list = await releases.GetAsync(c.App);
                if (!c.Busy) c.Refresh(list.Releases, list.Problem, state.For(c.App.Id));
            }).ToList();
            var launcherList = await releases.GetAsync(Launcher.Entry);
            await Task.WhenAll(tasks);

            launcherUpdate = LauncherRules.RunsInstalled(layout, AppContext.BaseDirectory) ? LauncherRules.UpdateFor(running, launcherList.Releases) : null;
            UpdateBanner.Visibility = launcherUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            if (launcherUpdate is { } u) UpdateBannerText.Text = $"A new version of NEKCA Apps is available ({u.Version}).";
            CheckedText.Text = $"Checked {DateTime.Now:h:mm tt}";
            CleanUpOldVersions();
        }
        finally { RefreshButton.IsEnabled = true; loading = false; }
    }

    private AppCard NewCard(AppEntry app)
    {
        var card = new AppCard(app);
        card.InstallCommand = new(() => InstallLatestAsync(card));
        card.UpdateCommand = new(() => InstallLatestAsync(card));
        card.OpenCommand = new(() => { Open(card); return Task.CompletedTask; });
        card.UninstallCommand = new(() => UninstallAsync(card));
        card.UseVersion = row => UseVersionAsync(card, row);
        card.RemoveVersion = row => RemoveVersionAsync(card, row);
        card.DesktopShortcutChanged += (c, on) => Try(c, () => installer.SetDesktopShortcut(c.App, on));
        card.Refresh([], null, state.For(app.Id));
        return card;
    }

    private Task InstallLatestAsync(AppCard card) =>
        card.Latest is { } latest ? SwitchToAsync(card, latest.Version.ToString(), latest) : Task.CompletedTask;

    private Task UseVersionAsync(AppCard card, VersionRow row) => SwitchToAsync(card, row.Version, row.Release);

    /// <summary>Downloads the version if needed, then makes it the one in use.</summary>
    private async Task SwitchToAsync(AppCard card, string version, Release? release)
    {
        if (card.Busy) return;
        card.Message = null;
        card.Busy = true;
        try
        {
            var wasRunning = IsRunning(card);
            if (installer.ExePath(card.App.Id, version) is null)
            {
                if (release is null) throw new InstallException($"Version {version} can't be downloaded any more.");
                card.ProgressText = $"Downloading version {version}…";
                var progress = new Progress<DownloadProgress>(p =>
                {
                    card.Progress = p.Fraction;
                    card.ProgressText = $"Downloading version {version}… {p.Done / 1048576.0:0.0} MB" + (p.Total is { } t ? $" of {t / 1048576.0:0.0} MB" : "");
                });
                await installer.InstallAsync(card.App, release, progress);
                card.ProgressText = "Unpacking…";
                card.Progress = null;
            }
            var stuck = installer.Activate(card.App, version);
            card.Message = wasRunning ? $"{card.Name} is open: close it and open it again to use version {version}." : null;
            if (stuck.Count > 0)
                card.Message = (card.Message is null ? "" : card.Message + " ") + "Older versions still in use will be removed next time.";
        }
        catch (InstallException e) { card.Message = e.Message; }
        finally
        {
            card.ProgressText = null;
            card.Progress = null;
            card.Busy = false;
            card.Refresh(card.Releases, card.ReleasesProblem, state.For(card.App.Id));
        }
    }

    private async Task RemoveVersionAsync(AppCard card, VersionRow row)
    {
        await Task.Yield();
        Try(card, () =>
        {
            if (!installer.Remove(card.App, row.Version))
                card.Message = $"Version {row.Version} is open. Close it and try again.";
        });
    }

    private async Task UninstallAsync(AppCard card)
    {
        await Task.Yield();
        if (MessageBox.Show(this, $"Remove {card.Name} from this computer? Its settings and data aren't touched, and it can be installed again.",
                "Uninstall", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Try(card, () =>
        {
            if (!installer.Uninstall(card.App)) card.Message = $"{card.Name} is open. Close it and try again.";
        });
    }

    private void Open(AppCard card)
    {
        if (card.Install.Active is not { } v || installer.ExePath(card.App.Id, v) is not { } exe) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); }
        catch (Exception e) { card.Message = "Windows couldn't start it: " + e.Message; }
    }

    private void Try(AppCard card, Action action)
    {
        card.Message = null;
        try { action(); }
        catch (InstallException e) { card.Message = e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { card.Message = e.Message; }
        card.Refresh(card.Releases, card.ReleasesProblem, state.For(card.App.Id));
    }

    /// <summary>Retries removing versions that were open when the app was last switched (keeps the in-use one and the newest other).</summary>
    private void CleanUpOldVersions()
    {
        foreach (var card in cards.Where(c => !c.Busy && c.Install.Installed.Count > 2))
        {
            var keep = card.Install.Installed.Keys.Where(v => v != card.Install.Active)
                .OrderByDescending(AppVersion.TryParse).FirstOrDefault();
            foreach (var v in AppStatusRules.ToClean(card.Install, keep)) installer.Remove(card.App, v);
            card.Refresh(card.Releases, card.ReleasesProblem, state.For(card.App.Id));
        }
    }

    private bool IsRunning(AppCard card)
    {
        try
        {
            var root = Path.GetFullPath(layout.AppFolder(card.App.Id));
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(card.App.Exe)))
            {
                using (p)
                {
                    try { if (p.MainModule?.FileName?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true) return true; }
                    catch (Exception) { /* another user's process */ }
                }
            }
        }
        catch (Exception) { }
        return false;
    }

    // ---- the launcher itself

    private void OnInstallLauncher(object sender, RoutedEventArgs e)
    {
        try
        {
            installer.InstallFromFolder(Launcher.Entry, running.ToString(), AppContext.BaseDirectory.TrimEnd('\\', '/'));
            state.For(Launcher.Entry.Id).DesktopShortcut = true;
            installer.Activate(Launcher.Entry, running.ToString());
            StartAndClose(installer.ExePath(Launcher.Entry.Id, running.ToString())!);
        }
        catch (Exception ex) when (ex is InstallException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, "NEKCA Apps couldn't be installed: " + ex.Message, "NEKCA Apps", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnUpdateLauncher(object sender, RoutedEventArgs e)
    {
        if (launcherUpdate is not { } update) return;
        UpdateBanner.IsEnabled = false;
        UpdateBannerText.Text = $"Downloading NEKCA Apps {update.Version}…";
        try
        {
            await installer.InstallAsync(Launcher.Entry, update);
            installer.Activate(Launcher.Entry, update.Version.ToString());
            StartAndClose(installer.ExePath(Launcher.Entry.Id, update.Version.ToString())!);
        }
        catch (InstallException ex)
        {
            UpdateBannerText.Text = "The update didn't work: " + ex.Message;
            UpdateBanner.IsEnabled = true;
        }
    }

    private static void StartAndClose(string exe)
    {
        // let go of the one-instance lock first, or the new copy would just show this window and quit
        App.Current.ReleaseForRestart();
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        App.Current.Quit();
    }

    private static AppVersion RunningVersion()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return AppVersion.TryParse(info) ?? new AppVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0));
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NEKCA-Apps/1.0 (+https://github.com/Nerves82/nekca-apps)");
        return http;
    }
}
