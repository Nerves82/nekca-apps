using System.Text.Json;
using System.Text.Json.Serialization;

namespace NekcaApps.Core;

public enum ThemeMode { System, Light, Dark }

/// <summary>The launcher's own preferences (settings.json in the install folder).</summary>
public sealed class LauncherSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Closing the window keeps NEKCA Apps running by the clock, checking for updates.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Start (in the tray) when Windows starts. Only takes effect once the launcher is installed.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>The "still running by the clock" tip has been shown once.</summary>
    public bool TrayTipShown { get; set; }

    /// <summary>The next theme when the theme button is clicked: System → Light → Dark → System.</summary>
    public static ThemeMode Next(ThemeMode m) => m switch { ThemeMode.System => ThemeMode.Light, ThemeMode.Light => ThemeMode.Dark, _ => ThemeMode.System };

    /// <summary>Dark or not, for a mode and whether Windows is set to dark apps.</summary>
    public static bool IsDark(ThemeMode m, bool windowsDark) => m == ThemeMode.Dark || (m == ThemeMode.System && windowsDark);

    private static string File(InstallLayout layout) => Path.Combine(layout.Root, "settings.json");

    public static LauncherSettings Load(InstallLayout layout)
    {
        try
        {
            if (System.IO.File.Exists(File(layout)))
                return JsonSerializer.Deserialize<LauncherSettings>(System.IO.File.ReadAllText(File(layout)), Catalog.Json) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new();
    }

    public void Save(InstallLayout layout)
    {
        try
        {
            Directory.CreateDirectory(layout.Root);
            System.IO.File.WriteAllText(File(layout), JsonSerializer.Serialize(this, Catalog.Json));
        }
        catch (IOException) { /* preferences are a convenience */ }
    }
}

/// <summary>Which updates the tray has already announced, so each is announced once.</summary>
public sealed class UpdateNotices
{
    private readonly HashSet<string> told = [];

    /// <summary>The updates not announced yet ("Fuel Assistance Intake 0.1.140"), and remembers them.</summary>
    public IReadOnlyList<string> New(IEnumerable<(string Name, AppVersion Version)> available)
    {
        var fresh = available.Where(a => told.Add($"{a.Name}|{a.Version}")).Select(a => $"{a.Name} {a.Version}").ToList();
        return fresh;
    }
}
