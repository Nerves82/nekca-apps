using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NekcaApps.Core;

/// <summary>
/// One app the launcher offers. Its versions are the GitHub Releases of <see cref="Repo"/>; each release has a zip
/// named by <see cref="Asset"/> that contains <see cref="Exe"/> (anywhere inside it).
/// </summary>
public sealed record AppEntry
{
    /// <summary>Stable id, used for the install folder. Never change it once people have the app installed.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>"owner/repo" on GitHub. Must be public.</summary>
    public string Repo { get; init; } = "";
    /// <summary>Release asset name with {version}, e.g. "FuelIntake-{version}-win-x64.zip".</summary>
    public string Asset { get; init; } = "";
    /// <summary>The program to start, e.g. "FuelIntake.exe".</summary>
    public string Exe { get; init; } = "";

    /// <summary>The asset name for a version.</summary>
    public string AssetFor(AppVersion v) => Asset.Replace("{version}", v.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Why the entry can't be used, or null when it's fine.</summary>
    public string? Problem()
    {
        if (!Catalog.IdPattern.IsMatch(Id)) return "id must be lower-case letters, digits and dashes";
        if (Name.Trim().Length == 0) return "name is missing";
        if (Repo.Split('/') is not [{ Length: > 0 }, { Length: > 0 }]) return "repo must be owner/name";
        if (!Asset.Contains("{version}", StringComparison.OrdinalIgnoreCase) || !Asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return "asset must be a .zip name containing {version}";
        if (!Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Exe.IndexOfAny(['/', '\\', ':']) >= 0) return "exe must be a file name ending .exe";
        return null;
    }
}

/// <summary>The launcher itself: updated from its own releases, like any app.</summary>
public static class Launcher
{
    public static readonly AppEntry Entry = new()
    {
        Id = "nekca-apps", Name = "NEKCA Apps", Description = "Installs and updates NEKCA's apps.",
        Repo = "Nerves82/nekca-apps", Asset = "NekcaApps-{version}-win-x64.zip", Exe = "NekcaApps.exe",
    };
}

/// <summary>The list of apps (apps.json in the nekca-apps repo).</summary>
public sealed record Catalog
{
    public List<AppEntry> Apps { get; init; } = [];

    /// <summary>Read online so new apps show up without a launcher update; the built-in copy is the fallback.</summary>
    public const string OnlineUrl = "https://raw.githubusercontent.com/Nerves82/nekca-apps/main/apps.json";

    internal static readonly System.Text.RegularExpressions.Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,40}$");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses apps.json, dropping entries that can't work (and duplicates of an id).</summary>
    public static Catalog Parse(string json)
    {
        var c = JsonSerializer.Deserialize<Catalog>(json, Json) ?? new Catalog();
        var seen = new HashSet<string>();
        return c with { Apps = c.Apps.Where(a => a.Problem() is null && seen.Add(a.Id)).ToList() };
    }

    public static Catalog BuiltIn()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("apps.json")!;
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }
}
