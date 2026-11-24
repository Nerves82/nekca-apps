namespace NekcaApps.Core.Tests;

public class LockedFileTests
{
    [Fact]
    public void Retry_WaitsOutABriefLock()
    {
        var waits = new List<TimeSpan>();
        var old = IoRetry.Wait;
        IoRetry.Wait = waits.Add;
        try
        {
            var tries = 0;
            IoRetry.Run(() => { if (++tries < 4) throw new UnauthorizedAccessException("scanner has it"); });
            Assert.Equal(4, tries);
            Assert.Equal(3, waits.Count);

            Assert.Throws<IOException>(() => IoRetry.Run(() => throw new IOException("stays locked"), attempts: 3));
            Assert.Throws<InvalidOperationException>(() => IoRetry.Run(() => throw new InvalidOperationException("not a lock")));
        }
        finally { IoRetry.Wait = old; }
    }

    private static Installer Make(InstallLayout layout) =>
        new(layout, InstallState.Load(layout),
            new HttpClient(new FakeHttp(req => FakeHttp.Bytes(Fakes.Zip(req.RequestUri!.Segments[^1].Split('-')[1])))), new FakeShortcuts());

    [Fact]
    public async Task Install_IntoAFolderAnEarlierTryLeftBehind()
    {
        var layout = new InstallLayout(Fakes.TempFolder());
        var target = layout.VersionFolder("demo-app", "1.0.0");
        Directory.CreateDirectory(Path.Combine(target, "Demo-1.0.0"));
        File.WriteAllText(Path.Combine(target, "Demo-1.0.0", "half-written.dll"), "x");   // a failed earlier try
        Directory.CreateDirectory(layout.VersionFolder("demo-app", "0.9.0") + ".partial");    // the old install style's leftover

        var i = Make(layout);
        await i.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        Assert.True(File.Exists(i.ExePath("demo-app", "1.0.0")));
        Assert.False(File.Exists(Path.Combine(target, "Demo-1.0.0", "half-written.dll")));
        Assert.Equal(["1.0.0"], Directory.GetDirectories(layout.AppFolder("demo-app")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task FailedInstall_LeavesNoFolder_AndIsntCountedAsInstalled()
    {
        var layout = new InstallLayout(Fakes.TempFolder());
        var i = new Installer(layout, InstallState.Load(layout),
            new HttpClient(new FakeHttp(_ => FakeHttp.Bytes(Fakes.Zip("1.0.0", exe: "Wrong.exe")))), new FakeShortcuts());
        await Assert.ThrowsAsync<InstallException>(() => i.InstallAsync(Fakes.App, Fakes.Release("1.0.0")));
        Assert.False(Directory.Exists(layout.VersionFolder("demo-app", "1.0.0")));
        Assert.Empty(i.State.For("demo-app").Installed);
    }

    [Fact]
    public async Task Leftovers_KeepInstalledVersions()
    {
        var layout = new InstallLayout(Fakes.TempFolder());
        var i = Make(layout);
        await i.InstallAsync(Fakes.App, Fakes.Release("1.0.0"));
        Directory.CreateDirectory(layout.VersionFolder("demo-app", "1.1.0") + ".removing-abc123");
        i.CleanUpLeftovers("demo-app");
        Assert.Equal(["1.0.0"], Directory.GetDirectories(layout.AppFolder("demo-app")).Select(Path.GetFileName));
    }
}
