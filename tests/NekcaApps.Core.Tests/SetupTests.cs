namespace NekcaApps.Core.Tests;

public class SetupTests
{
    private static readonly AppVersion V5 = AppVersion.TryParse("0.1.5")!;

    [Theory]
    [InlineData(@"C:\Users\x\Downloads\NekcaAppsSetup-0.1.5.exe", false, null, SetupAction.Install)]
    [InlineData(@"C:\Users\x\Downloads\NekcaAppsSetup (1).exe", false, "0.1.4", SetupAction.Install)]          // older installed: update
    [InlineData(@"C:\Users\x\Downloads\nekcaappssetup.exe", false, "0.1.5", SetupAction.OpenInstalled)]        // same version
    [InlineData(@"C:\Users\x\Downloads\NekcaAppsSetup.exe", false, "0.1.9", SetupAction.OpenInstalled)]        // newer installed
    [InlineData(@"C:\Users\x\Downloads\NekcaApps-0.1.5\NekcaApps.exe", false, null, SetupAction.Run)]           // unzipped copy: banner
    [InlineData(@"C:\Users\x\AppData\Local\NEKCA Apps\apps\nekca-apps\0.1.5\NekcaApps.exe", true, "0.1.5", SetupAction.Run)]
    public void Decide(string exe, bool installed, string? active, SetupAction expected) =>
        Assert.Equal(expected, SetupRules.Decide(exe, installed, V5, active));

    [Fact]
    public void InstallFromFile_CopiesJustTheProgram_UnderItsRealName()
    {
        var layout = new InstallLayout(Fakes.TempFolder());
        var shortcuts = new FakeShortcuts();
        var i = new Installer(layout, InstallState.Load(layout), new HttpClient(new FakeHttp(_ => throw new InvalidOperationException())), shortcuts);
        var downloads = Fakes.TempFolder();
        var setup = Path.Combine(downloads, "NekcaAppsSetup-0.1.5.exe");
        File.WriteAllText(setup, "fake program");
        File.WriteAllText(Path.Combine(downloads, "someone's other download.pdf"), "not ours");

        i.InstallFromFile(Launcher.Entry, "0.1.5", setup);
        var folder = layout.VersionFolder("nekca-apps", "0.1.5");
        Assert.Equal(["NekcaApps.exe"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Equal(Path.Combine(folder, "NekcaApps.exe"), i.ExePath("nekca-apps", "0.1.5"));
        Assert.True(LauncherRules.RunsInstalled(layout, folder));

        i.Activate(Launcher.Entry, "0.1.5");
        Assert.Equal(i.ExePath("nekca-apps", "0.1.5"), shortcuts.Set["NEKCA Apps"].Exe);
    }

    [Fact]
    public void InstalledAppsEntry_UninstallsThroughTheLauncher()
    {
        var e = SetupRules.InstalledAppsEntry(@"C:\L\NekcaApps.exe", V5, @"C:\L", 70_000);
        Assert.Equal("NEKCA Apps", e["DisplayName"]);
        Assert.Equal("0.1.5", e["DisplayVersion"]);
        Assert.Equal("\"C:\\L\\NekcaApps.exe\" --uninstall", e["UninstallString"]);
        Assert.Equal(70_000, e["EstimatedSize"]);
    }
}
