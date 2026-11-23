using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;

namespace NekcaApps.Core.Tests;

internal static class Fakes
{
    public static readonly AppEntry App = new()
    {
        Id = "demo-app", Name = "Demo App", Repo = "example/demo-app", Asset = "Demo-{version}-win-x64.zip", Exe = "Demo.exe",
    };

    /// <summary>A GitHub releases answer: each (tag, assets, prerelease, draft).</summary>
    public static string ReleasesJson(params (string Tag, string[] Assets, bool Pre, bool Draft)[] rels)
    {
        var arr = new JsonArray();
        var day = 1;
        foreach (var r in rels)
        {
            var assets = new JsonArray();
            foreach (var a in r.Assets)
                assets.Add(new JsonObject { ["name"] = a, ["browser_download_url"] = "https://example.test/dl/" + a, ["size"] = 100 });
            arr.Add(new JsonObject
            {
                ["tag_name"] = r.Tag, ["name"] = "Demo " + r.Tag, ["draft"] = r.Draft, ["prerelease"] = r.Pre,
                ["published_at"] = new DateTimeOffset(2026, 11, day++, 12, 0, 0, TimeSpan.Zero).ToString("O"),
                ["body"] = "## What's Changed\n* Add a fake feature by @someone in https://example.test/pull/1\n", ["assets"] = assets,
                ["html_url"] = "https://example.test/releases/" + r.Tag,
            });
        }
        return arr.ToJsonString();
    }

    public static (string Tag, string[] Assets, bool Pre, bool Draft) Rel(string version, bool pre = false, bool draft = false) =>
        ("v" + version, [$"Demo-{version}-win-x64.zip", "Demo-win-x64.zip"], pre, draft);

    /// <summary>A zip holding "Demo-{version}/Demo.exe" (fake bytes), like the real releases.</summary>
    public static byte[] Zip(string version, string exe = "Demo.exe")
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(z.CreateEntry($"Demo-{version}/{exe}").Open())) w.Write("not really a program " + version);
            using (var w = new StreamWriter(z.CreateEntry($"Demo-{version}/readme.txt").Open())) w.Write("fake");
        }
        return ms.ToArray();
    }

    public static Release Release(string version, string? sha = null) =>
        new(AppVersion.TryParse(version)!, "v" + version, "Demo " + version, DateTimeOffset.Now, "", false,
            new ReleaseAsset($"Demo-{version}-win-x64.zip", $"https://example.test/dl/Demo-{version}-win-x64.zip", 0, sha), "");

    public static string TempFolder()
    {
        var p = Path.Combine(Path.GetTempPath(), "nekca-apps-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }
}

/// <summary>Answers HTTP requests from a function; records what was asked.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(answer(request));
    }

    public static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
}

internal sealed class FakeShortcuts : IShortcuts
{
    public Dictionary<string, (string Exe, bool Desktop)> Set { get; } = [];
    void IShortcuts.Set(string name, string exePath, bool desktop) => Set[name] = (exePath, desktop);
    public void Remove(string name) => Set.Remove(name);
}
