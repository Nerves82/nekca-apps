using System.Net;
using System.Security.Cryptography;

namespace NekcaApps.Core.Tests;

public class InstallerTests
{
    private static (Installer Installer, FakeShortcuts Shortcuts, InstallLayout Layout, FakeHttp Http) Make(Func<HttpRequestMessage, HttpResponseMessage>? answer = null)
    {
        var layout = new InstallLayout(Fakes.TempFolder());
        var shortcuts = new FakeShortcuts();
        var http = new FakeHttp(answer ?? (req =>
        {
            var name = req.RequestUri!.Segments[^1];       // Demo-1.0.0-win-x64.zip
            return FakeHttp.Bytes(Fakes.Zip(name.Split('-')[1]));
        }));
        return (new Installer(layout, InstallState.Load(layout), new HttpClient(http), shortcuts), shortcuts, layout, http);
    }

    [Fact]
    public async Task Install_UnpacksIntoAVersionFolder_AndFindsTheProgram()
    {
        var (inst, shortcuts, layout, _) = Make();
        await inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        var exe = inst.ExePath("demo-app", "1.0.0")!;
        Assert.True(File.Exists(exe));
        Assert.Equal(Path.Combine(layout.VersionFolder("demo-app", "1.0.0"), "Demo-1.0.0", "Demo.exe"), exe);
        Assert.Null(inst.State.For("demo-app").Active);    // installing alone doesn't switch
        Assert.Empty(shortcuts.Set);
        Assert.Empty(Directory.GetFiles(layout.DownloadFolder)); // the zip is cleaned up
        Assert.True(InstallState.Load(layout).For("demo-app").Installed.ContainsKey("1.0.0")); // saved
    }

    [Fact]
    public async Task Activate_PointsShortcuts_AndKeepsOnlyThePreviousVersion()
    {
        var (inst, shortcuts, layout, _) = Make();
        foreach (var v in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            await inst.InstallAsync(Fakes.App, Fakes.Release(v));
            inst.Activate(Fakes.App, v);
        }
        var install = inst.State.For("demo-app");
        Assert.Equal("1.2.0", install.Active);
        Assert.Equal(["1.1.0", "1.2.0"], install.Installed.Keys.Order());
        Assert.False(Directory.Exists(layout.VersionFolder("demo-app", "1.0.0")));
        Assert.Equal(inst.ExePath("demo-app", "1.2.0"), shortcuts.Set["Demo App"].Exe);

        // going back to an older version keeps the newer one too, so it's one click to return
        await inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        inst.Activate(Fakes.App, "1.0.0");
        Assert.Equal(["1.0.0", "1.2.0"], install.Installed.Keys.Order());
    }

    [Fact]
    public async Task Install_AlreadyInstalled_DoesntDownloadAgain()
    {
        var (inst, _, _, http) = Make();
        await inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        await inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Install_BadChecksum_LeavesNothingBehind()
    {
        var (inst, _, layout, _) = Make();
        var e = await Assert.ThrowsAsync<InstallException>(() => inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0", sha: new string('0', 64))));
        Assert.Contains("checksum", e.Message);
        Assert.False(Directory.Exists(layout.VersionFolder("demo-app", "1.0.0")));
        Assert.Empty(inst.State.For("demo-app").Installed);
    }

    [Fact]
    public async Task Install_GoodChecksum_Passes()
    {
        var zip = Fakes.Zip("1.0.0");
        var (inst, _, _, _) = Make(_ => FakeHttp.Bytes(zip));
        await inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0", sha: Convert.ToHexStringLower(SHA256.HashData(zip))));
        Assert.NotNull(inst.ExePath("demo-app", "1.0.0"));
    }

    [Fact]
    public async Task Install_ZipWithoutTheProgram_Fails()
    {
        var (inst, _, layout, _) = Make(_ => FakeHttp.Bytes(Fakes.Zip("1.0.0", exe: "Other.exe")));
        var e = await Assert.ThrowsAsync<InstallException>(() => inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0")));
        Assert.Contains("Demo.exe", e.Message);
        Assert.False(Directory.Exists(layout.VersionFolder("demo-app", "1.0.0")));
    }

    [Fact]
    public async Task Install_HttpError_IsExplained()
    {
        var (inst, _, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var e = await Assert.ThrowsAsync<InstallException>(() => inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0")));
        Assert.Contains("404", e.Message);
    }

    [Fact]
    public async Task Remove_RefusesTheVersionInUse()
    {
        var (inst, _, _, _) = Make();
        await inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        inst.Activate(Fakes.App, "1.0.0");
        Assert.Throws<InstallException>(() => inst.Remove(Fakes.App, "1.0.0"));
    }

    [Fact]
    public async Task Uninstall_RemovesEverything_AndTheShortcuts()
    {
        var (inst, shortcuts, layout, _) = Make();
        await inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        inst.Activate(Fakes.App, "1.0.0");
        inst.SetDesktopShortcut(Fakes.App, true);
        Assert.True(shortcuts.Set["Demo App"].Desktop);
        Assert.True(inst.Uninstall(Fakes.App));
        Assert.Empty(shortcuts.Set);
        Assert.False(inst.State.Apps.ContainsKey("demo-app"));
        Assert.Empty(Directory.GetDirectories(layout.AppFolder("demo-app")));
    }

    [Fact]
    public async Task Reconcile_ForgetsFoldersDeletedByHand()
    {
        var (inst, _, layout, _) = Make();
        await inst.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        inst.Activate(Fakes.App, "1.0.0");
        Directory.Delete(layout.VersionFolder("demo-app", "1.0.0"), true);
        var s = InstallState.Load(layout);
        s.Reconcile(layout);
        Assert.Null(s.For("demo-app").Active);
        Assert.Empty(s.For("demo-app").Installed);
    }

    [Fact]
    public void InstallFromFolder_CopiesTheRunningLauncher()
    {
        var (inst, _, layout, _) = Make();
        var src = Fakes.TempFolder();
        File.WriteAllText(Path.Combine(src, "NekcaApps.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(src, "runtimes"));
        File.WriteAllText(Path.Combine(src, "runtimes", "x.dll"), "fake");
        inst.InstallFromFolder(Launcher.Entry, "0.1.5", src);
        var exe = inst.ExePath("nekca-apps", "0.1.5")!;
        Assert.True(File.Exists(exe));
        Assert.True(File.Exists(Path.Combine(layout.VersionFolder("nekca-apps", "0.1.5"), "runtimes", "x.dll")));
        Assert.True(LauncherRules.RunsInstalled(layout, Path.GetDirectoryName(exe)!));
        Assert.False(LauncherRules.RunsInstalled(layout, src));
    }

    [Theory]
    [InlineData(null, AppStatus.NotInstalled)]
    [InlineData("1.0.0", AppStatus.UpdateAvailable)]
    [InlineData("1.1.0", AppStatus.UpToDate)]
    [InlineData("2.0.0", AppStatus.NewerThanLatest)]
    public void Status_ComparesWithTheLatestRelease(string? active, AppStatus expected)
    {
        var releases = GitHubReleases.Parse(Fakes.ReleasesJson(Fakes.Rel("1.0.0"), Fakes.Rel("1.1.0"), Fakes.Rel("2.0.0", pre: true)), Fakes.App);
        Assert.Equal(expected, AppStatusRules.Status(new AppInstall { Active = active }, releases));
        Assert.Equal(active is null ? AppStatus.NotInstalled : AppStatus.Unknown, AppStatusRules.Status(new AppInstall { Active = active }, []));
    }

    [Fact]
    public void LauncherUpdate_OnlyWhenNewer()
    {
        var releases = GitHubReleases.Parse(Fakes.ReleasesJson(Fakes.Rel("0.1.4"), Fakes.Rel("0.1.6")), Fakes.App);
        Assert.Equal("0.1.6", LauncherRules.UpdateFor(AppVersion.TryParse("0.1.5")!, releases)?.Version.ToString());
        Assert.Null(LauncherRules.UpdateFor(AppVersion.TryParse("0.1.6")!, releases));
    }
}

public class LauncherUninstallTests
{
    private static (Installer I, FakeShortcuts S, InstallLayout L) Make()
    {
        var layout = new InstallLayout(Fakes.TempFolder());
        var shortcuts = new FakeShortcuts();
        var http = new FakeHttp(req => FakeHttp.Bytes(Fakes.Zip(req.RequestUri!.Segments[^1].Split('-')[1])));
        return (new Installer(layout, InstallState.Load(layout), new HttpClient(http), shortcuts), shortcuts, layout);
    }

    private static void InstallLauncher(Installer i)
    {
        var src = Fakes.TempFolder();
        File.WriteAllText(Path.Combine(src, "NekcaApps.exe"), "fake");
        i.InstallFromFolder(Launcher.Entry, "0.1.5", src);
        i.Activate(Launcher.Entry, "0.1.5");
    }

    [Fact]
    public async Task WithApps_RemovesEverything_AndTheWholeFolderAfterExit()
    {
        var (i, s, layout) = Make();
        InstallLauncher(i);
        await i.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        i.Activate(Fakes.App, "1.0.0");
        Assert.Equal(2, s.Set.Count);

        var r = i.UninstallLauncher([Fakes.App], removeApps: true);
        Assert.Empty(s.Set);
        Assert.Empty(r.AppsStillOpen);
        Assert.Equal([layout.Root], r.DeleteAfterExit);
        Assert.Empty(InstallState.Load(layout).Apps);
    }

    [Fact]
    public async Task WithoutApps_KeepsTheAppsAndTheirShortcuts()
    {
        var (i, s, layout) = Make();
        InstallLauncher(i);
        await i.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        i.Activate(Fakes.App, "1.0.0");

        var r = i.UninstallLauncher([Fakes.App], removeApps: false);
        Assert.Equal(["Demo App"], s.Set.Keys);
        Assert.Contains(layout.AppFolder("nekca-apps"), r.DeleteAfterExit);
        Assert.DoesNotContain(layout.Root, r.DeleteAfterExit);
        Assert.DoesNotContain(layout.StateFile, r.DeleteAfterExit);
        Assert.All(r.DeleteAfterExit, p => Assert.StartsWith(layout.Root, p));
        var state = InstallState.Load(layout);
        Assert.False(state.Apps.ContainsKey("nekca-apps"));
        Assert.Equal("1.0.0", state.For("demo-app").Active);
    }
}
