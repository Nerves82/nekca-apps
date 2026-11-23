using System.Net;

namespace NekcaApps.Core.Tests;

public class CatalogAndReleaseTests
{
    [Fact]
    public void BuiltInCatalog_HasTheFuelApp()
    {
        var c = Catalog.BuiltIn();
        var fuel = Assert.Single(c.Apps, a => a.Id == "fuel-intake");
        Assert.Equal("Nerves82/fuel-intake-app", fuel.Repo);
        Assert.Equal("FuelIntake-0.1.132-win-x64.zip", fuel.AssetFor(AppVersion.TryParse("v0.1.132")!));
        Assert.Null(Launcher.Entry.Problem());
    }

    [Fact]
    public void Catalog_DropsBrokenAndDuplicateEntries()
    {
        var c = Catalog.Parse("""
            { "apps": [
              { "id": "good", "name": "Good", "repo": "a/b", "asset": "G-{version}.zip", "exe": "G.exe" },
              { "id": "good", "name": "Dup", "repo": "a/c", "asset": "D-{version}.zip", "exe": "D.exe" },
              { "id": "Bad Id", "name": "X", "repo": "a/b", "asset": "X-{version}.zip", "exe": "X.exe" },
              { "id": "noversion", "name": "X", "repo": "a/b", "asset": "X.zip", "exe": "X.exe" },
              { "id": "path-exe", "name": "X", "repo": "a/b", "asset": "X-{version}.zip", "exe": "..\\X.exe" },
              { "id": "norepo", "name": "X", "repo": "ab", "asset": "X-{version}.zip", "exe": "X.exe" }
            ] }
            """);
        Assert.Equal(["Good"], c.Apps.Select(a => a.Name));
    }

    [Theory]
    [InlineData("v0.1.132", "0.1.132")]
    [InlineData("1.2", "1.2")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    [InlineData("latest", null)]
    [InlineData("", null)]
    public void Version_FromTag(string tag, string? expected) => Assert.Equal(expected, AppVersion.TryParse(tag)?.ToString());

    [Fact]
    public void Version_OrdersNumerically()
    {
        Assert.True(AppVersion.TryParse("0.1.10")! > AppVersion.TryParse("0.1.9")!);
        Assert.Equal(0, AppVersion.TryParse("1.2")!.CompareTo(AppVersion.TryParse("1.2.0")));
    }

    [Fact]
    public void Releases_NewestFirst_SkippingDraftsAndOtherZips()
    {
        var json = Fakes.ReleasesJson(Fakes.Rel("0.1.9"), Fakes.Rel("0.1.12"), Fakes.Rel("0.1.13", draft: true),
            ("v0.1.11", ["Other-0.1.11.zip"], false, false), ("nightly", ["Demo-nightly-win-x64.zip"], false, false), Fakes.Rel("0.2.0", pre: true));
        var r = GitHubReleases.Parse(json, Fakes.App);
        Assert.Equal(["0.2.0", "0.1.12", "0.1.9"], r.Select(x => x.Version.ToString()));
        Assert.Equal("0.1.12", GitHubReleases.Latest(r)!.Version.ToString()); // pre-releases aren't "latest"
        Assert.Equal("https://example.test/dl/Demo-0.1.12-win-x64.zip", r[1].Asset.Url);
        Assert.Equal(["Add a fake feature"], r[1].Changes);
    }

    [Fact]
    public void Releases_ReadTheChecksum()
    {
        var json = """[{"tag_name":"v1.0.0","assets":[{"name":"Demo-1.0.0-win-x64.zip","browser_download_url":"u","size":5,"digest":"sha256:ABCDEF"}]}]""";
        Assert.Equal("abcdef", GitHubReleases.Parse(json, Fakes.App)[0].Asset.Sha256);
    }

    [Fact]
    public async Task ReleaseSource_UsesETag_AndFallsBackToTheSavedCopy()
    {
        var folder = Fakes.TempFolder();
        var body = Fakes.ReleasesJson(Fakes.Rel("1.0.0"));
        var mode = "ok";
        var http = new FakeHttp(req => mode switch
        {
            "ok" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), Headers = { ETag = new("\"abc\"") } },
            "same" => req.Headers.IfNoneMatch.Any(t => t.Tag == "\"abc\"") ? new HttpResponseMessage(HttpStatusCode.NotModified) : new HttpResponseMessage(HttpStatusCode.OK),
            "limited" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => throw new HttpRequestException("offline"),
        });
        var src = new GitHubReleaseSource(new HttpClient(http), folder);

        var first = await src.GetAsync(Fakes.App);
        Assert.False(first.FromCache);
        Assert.Single(first.Releases);

        mode = "same";
        var second = await src.GetAsync(Fakes.App);
        Assert.False(second.FromCache);
        Assert.Single(second.Releases);

        mode = "limited";
        var third = await src.GetAsync(Fakes.App);
        Assert.True(third.FromCache);
        Assert.Contains("limiting", third.Problem);
        Assert.Single(third.Releases);

        mode = "offline";
        var fourth = await src.GetAsync(Fakes.App);
        Assert.True(fourth.FromCache);
        Assert.Single(fourth.Releases);

        var empty = await new GitHubReleaseSource(new HttpClient(http), Fakes.TempFolder()).GetAsync(Fakes.App);
        Assert.Empty(empty.Releases);
        Assert.NotNull(empty.Problem);
    }
}
