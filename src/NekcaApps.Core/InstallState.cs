using System.Text.Json;

namespace NekcaApps.Core;

/// <summary>
/// Where things go. Everything is per user under %LOCALAPPDATA%\NEKCA Apps, so nothing needs admin rights:
/// <code>
/// NEKCA Apps\state.json                     which versions are installed and in use
/// NEKCA Apps\apps\fuel-intake\0.1.132\...   one folder per installed version
/// NEKCA Apps\apps\nekca-apps\0.1.7\...     the launcher itself, installed like any app
/// NEKCA Apps\cache, downloads               release lists; zips while downloading
/// </code>
/// </summary>
public sealed class InstallLayout(string root)
{
    public string Root { get; } = root;
    public string StateFile => Path.Combine(Root, "state.json");
    public string CacheFolder => Path.Combine(Root, "cache");
    public string DownloadFolder => Path.Combine(Root, "downloads");
    public string AppFolder(string appId) => Path.Combine(Root, "apps", appId);
    public string VersionFolder(string appId, string version) => Path.Combine(AppFolder(appId), version);

    public static InstallLayout ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEKCA Apps"));
}

/// <summary>What's installed for one app.</summary>
public sealed class AppInstall
{
    /// <summary>The version that "Open" and the shortcuts start; null when none is.</summary>
    public string? Active { get; set; }
    /// <summary>Installed versions, each with its program's path relative to the version folder.</summary>
    public Dictionary<string, string> Installed { get; set; } = [];
    public bool DesktopShortcut { get; set; }
}

public sealed class InstallState
{
    public Dictionary<string, AppInstall> Apps { get; set; } = [];

    public AppInstall For(string appId) => Apps.TryGetValue(appId, out var a) ? a : Apps[appId] = new AppInstall();

    public static InstallState Load(InstallLayout layout)
    {
        try
        {
            if (File.Exists(layout.StateFile))
                return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(layout.StateFile), Catalog.Json) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new();
    }

    public void Save(InstallLayout layout)
    {
        Directory.CreateDirectory(layout.Root);
        var temp = layout.StateFile + ".saving";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Catalog.Json));
        File.Move(temp, layout.StateFile, overwrite: true);
    }

    /// <summary>Drops versions whose folder is gone (deleted by hand), and an active version that isn't installed.</summary>
    public void Reconcile(InstallLayout layout)
    {
        foreach (var (id, app) in Apps)
        {
            foreach (var v in app.Installed.Keys.ToList())
                if (!File.Exists(Path.Combine(layout.VersionFolder(id, v), app.Installed[v]))) app.Installed.Remove(v);
            if (app.Active is not null && !app.Installed.ContainsKey(app.Active)) app.Active = null;
        }
    }
}

public enum AppStatus { NotInstalled, UpToDate, UpdateAvailable, NewerThanLatest, Unknown }

public static class AppStatusRules
{
    /// <summary>
    /// Installed and up to date, an update waiting, or not installed. "Newer than latest" is a version someone picked
    /// that is a pre-release or that the latest release is older than; "Unknown" means no releases could be read.
    /// </summary>
    public static AppStatus Status(AppInstall install, IReadOnlyList<Release> releases)
    {
        if (install.Active is null) return AppStatus.NotInstalled;
        var latest = GitHubReleases.Latest(releases);
        if (latest is null || AppVersion.TryParse(install.Active) is not { } active) return AppStatus.Unknown;
        var c = latest.Version.CompareTo(active);
        return c > 0 ? AppStatus.UpdateAvailable : c == 0 ? AppStatus.UpToDate : AppStatus.NewerThanLatest;
    }

    /// <summary>
    /// After switching versions, the versions to remove: everything except the one now in use and the one used
    /// just before (kept so going back is instant). Any older version can still be reinstalled from the list.
    /// </summary>
    public static IReadOnlyList<string> ToClean(AppInstall install, string? previous) =>
        install.Installed.Keys.Where(v => v != install.Active && v != previous).ToList();
}
