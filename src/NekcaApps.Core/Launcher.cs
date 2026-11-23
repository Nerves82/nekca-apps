namespace NekcaApps.Core;

/// <summary>How the running launcher relates to its installed copy.</summary>
public static class LauncherRules
{
    /// <summary>True when the launcher runs from a version folder in the install folder (not from a downloaded zip).</summary>
    public static bool RunsInstalled(InstallLayout layout, string runningFolder)
    {
        var apps = Path.GetFullPath(layout.AppFolder(Launcher.Entry.Id)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(runningFolder).StartsWith(apps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A newer launcher release than the running one, or null.</summary>
    public static Release? UpdateFor(AppVersion running, IReadOnlyList<Release> releases) =>
        GitHubReleases.Latest(releases) is { } latest && latest.Version > running ? latest : null;
}
