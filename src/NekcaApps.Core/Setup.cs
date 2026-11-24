namespace NekcaApps.Core;

/// <summary>What the launcher does when it starts.</summary>
public enum SetupAction
{
    /// <summary>Run normally (installed, or an unzipped copy that shows the "Install NEKCA Apps" banner).</summary>
    Run,
    /// <summary>The setup download, and this version isn't installed or is newer: install it and switch to it.</summary>
    Install,
    /// <summary>The setup download, but the same or a newer version is installed: just open that one.</summary>
    OpenInstalled,
}

public static class SetupRules
{
    /// <summary>The setup download's file name starts with this (browsers may add " (1)" and so on).</summary>
    public const string SetupPrefix = "NekcaAppsSetup";

    public static bool IsSetupFile(string? exePath)
    {
        var path = exePath ?? "";
        var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static SetupAction Decide(string? exePath, bool runsInstalled, AppVersion running, string? installedActive)
    {
        if (runsInstalled || !IsSetupFile(exePath)) return SetupAction.Run;
        return AppVersion.TryParse(installedActive) is { } active && active.CompareTo(running) >= 0
            ? SetupAction.OpenInstalled
            : SetupAction.Install;
    }

    /// <summary>
    /// The values of the per-user entry in Windows' Installed apps list
    /// (HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\NekcaApps), which needs no admin rights.
    /// </summary>
    public static IReadOnlyDictionary<string, object> InstalledAppsEntry(string exePath, AppVersion version, string installFolder, long sizeKb) =>
        new Dictionary<string, object>
        {
            ["DisplayName"] = "NEKCA Apps",
            ["DisplayVersion"] = version.ToString(),
            ["Publisher"] = "NEKCA",
            ["DisplayIcon"] = $"\"{exePath}\",0",
            ["InstallLocation"] = installFolder,
            ["UninstallString"] = $"\"{exePath}\" --uninstall",
            ["URLInfoAbout"] = "https://github.com/Nerves82/nekca-apps",
            ["NoModify"] = 1,
            ["NoRepair"] = 1,
            ["EstimatedSize"] = (int)Math.Min(int.MaxValue, Math.Max(1, sizeKb)),
        };

    public const string InstalledAppsKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NekcaApps";
}
