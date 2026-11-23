using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NekcaApps.Core;

/// <summary>A downloadable zip of one release.</summary>
/// <param name="Sha256">Lower-case hex SHA-256 from GitHub, when GitHub provides it.</param>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>One version of an app (a GitHub Release with the app's zip).</summary>
public sealed record Release(AppVersion Version, string Tag, string Title, DateTimeOffset Published, string Notes,
    bool Prerelease, ReleaseAsset Asset, string PageUrl)
{
    /// <summary>The release notes as short lines: GitHub's "What's Changed" items without the "by @user in …" tail.</summary>
    public IReadOnlyList<string> Changes => ReleaseNotes.Changes(Notes);
}

public static class ReleaseNotes
{
    public static IReadOnlyList<string> Changes(string? notes)
    {
        var list = new List<string>();
        foreach (var raw in (notes ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("* ") && !line.StartsWith("- ")) continue;
            line = line[2..].Trim();
            var by = line.LastIndexOf(" by @", StringComparison.Ordinal);
            if (by > 0) line = line[..by].Trim();
            if (line.StartsWith("@", StringComparison.Ordinal) && line.Contains(" made their first contribution")) continue;
            if (line.Length > 0) list.Add(line);
        }
        return list;
    }
}

public static class GitHubReleases
{
    /// <summary>
    /// The app's releases, newest first: drafts, releases without the app's zip and tags that aren't versions are
    /// left out. One release per version (the newest-published wins).
    /// </summary>
    public static IReadOnlyList<Release> Parse(string json, AppEntry app)
    {
        var list = new List<Release>();
        if (JsonNode.Parse(json) is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            if (n is null || (bool?)n["draft"] == true) continue;
            var tag = (string?)n["tag_name"] ?? "";
            if (AppVersion.TryParse(tag) is not { } v) continue;
            var want = app.AssetFor(v);
            var a = (n["assets"] as JsonArray)?.FirstOrDefault(x => string.Equals((string?)x?["name"], want, StringComparison.OrdinalIgnoreCase));
            if (a is null) continue;
            var digest = (string?)a["digest"];
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
            list.Add(new Release(v, tag, (string?)n["name"] ?? tag,
                DateTimeOffset.TryParse((string?)n["published_at"], out var at) ? at : DateTimeOffset.MinValue,
                (string?)n["body"] ?? "", (bool?)n["prerelease"] == true,
                new ReleaseAsset(want, (string?)a["browser_download_url"] ?? "", (long?)a["size"] ?? 0, sha),
                (string?)n["html_url"] ?? ""));
        }
        return list.GroupBy(r => r.Version.ToString()).Select(g => g.OrderByDescending(r => r.Published).First())
            .OrderByDescending(r => r.Version).ThenByDescending(r => r.Published).ToList();
    }

    /// <summary>The newest release that isn't a pre-release, or null.</summary>
    public static Release? Latest(IEnumerable<Release> releases) =>
        releases.Where(r => !r.Prerelease).OrderByDescending(r => r.Version).FirstOrDefault();
}

/// <summary>Releases as last read, and whether they came from GitHub just now or from the saved copy.</summary>
public sealed record ReleaseList(IReadOnlyList<Release> Releases, bool FromCache, DateTimeOffset? CheckedAt, string? Problem);

/// <summary>
/// Reads releases from the GitHub API without signing in. Answers are saved with their ETag, so asking again only
/// costs a "not modified" reply (which GitHub doesn't count against the 60-an-hour limit for an office's address),
/// and the saved copy is used when GitHub can't be reached.
/// </summary>
public sealed class GitHubReleaseSource(HttpClient http, string cacheFolder)
{
    private sealed record Cached(string? ETag, string Body, DateTimeOffset At);

    public async Task<ReleaseList> GetAsync(AppEntry app, CancellationToken ct = default)
    {
        var file = Path.Combine(cacheFolder, "releases-" + app.Repo.Replace('/', '_') + ".json");
        Cached? cached = null;
        try { if (File.Exists(file)) cached = JsonSerializer.Deserialize<Cached>(await File.ReadAllTextAsync(file, ct)); }
        catch (Exception e) when (e is IOException or JsonException) { cached = null; }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{app.Repo}/releases?per_page=100");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (cached?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tagValue)) req.Headers.IfNoneMatch.Add(tagValue);
            using var res = await http.SendAsync(req, ct);
            var now = DateTimeOffset.Now;
            if (res.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                await SaveAsync(file, cached with { At = now }, ct);
                return new(GitHubReleases.Parse(cached.Body, app), false, now, null);
            }
            if (!res.IsSuccessStatusCode)
                return Fallback(cached, app, res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                    ? "GitHub is limiting requests from this network for a while"
                    : $"GitHub answered {(int)res.StatusCode}");
            var body = await res.Content.ReadAsStringAsync(ct);
            await SaveAsync(file, new Cached(res.Headers.ETag?.ToString(), body, now), ct);
            return new(GitHubReleases.Parse(body, app), false, now, null);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return Fallback(cached, app, "couldn't reach GitHub (offline?)");
        }
    }

    private static ReleaseList Fallback(object? cached, AppEntry app, string problem) => cached is Cached c
        ? new(GitHubReleases.Parse(c.Body, app), true, c.At, problem)
        : new([], true, null, problem);

    private async Task SaveAsync(string file, Cached c, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(cacheFolder);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(c), ct);
        }
        catch (IOException) { /* the cache is a convenience */ }
    }
}
