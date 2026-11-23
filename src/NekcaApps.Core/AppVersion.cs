namespace NekcaApps.Core;

/// <summary>A release version like 0.1.132 (from the tag "v0.1.132"). Anything after "-" or "+" is ignored for ordering.</summary>
public sealed record AppVersion(Version Number) : IComparable<AppVersion>
{
    public static AppVersion? TryParse(string? tag)
    {
        var s = tag?.Trim() ?? "";
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!s.Contains('.')) return null;
        return Version.TryParse(s, out var v) ? new AppVersion(v) : null;
    }

    public int CompareTo(AppVersion? other) => other is null ? 1 : Normal(Number).CompareTo(Normal(other.Number));

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => Number.ToString();

    // 1.2 and 1.2.0 are the same version
    private static Version Normal(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
