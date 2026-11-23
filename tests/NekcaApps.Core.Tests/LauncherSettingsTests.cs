namespace NekcaApps.Core.Tests;

public class LauncherSettingsTests
{
    [Fact]
    public void Settings_SaveAndLoad_WithSafeDefaults()
    {
        var layout = new InstallLayout(Fakes.TempFolder());
        var fresh = LauncherSettings.Load(layout);
        Assert.Equal(ThemeMode.System, fresh.Theme);
        Assert.True(fresh.CloseToTray);
        Assert.False(fresh.StartWithWindows);

        fresh.Theme = ThemeMode.Dark;
        fresh.StartWithWindows = true;
        fresh.Save(layout);
        var again = LauncherSettings.Load(layout);
        Assert.Equal(ThemeMode.Dark, again.Theme);
        Assert.True(again.StartWithWindows);
        Assert.Contains("\"Dark\"", File.ReadAllText(Path.Combine(layout.Root, "settings.json")));

        File.WriteAllText(Path.Combine(layout.Root, "settings.json"), "{ not json");
        Assert.Equal(ThemeMode.System, LauncherSettings.Load(layout).Theme);
    }

    [Theory]
    [InlineData(ThemeMode.System, false, false)]
    [InlineData(ThemeMode.System, true, true)]
    [InlineData(ThemeMode.Light, true, false)]
    [InlineData(ThemeMode.Dark, false, true)]
    public void Theme_FollowsWindowsOnlyOnSystem(ThemeMode mode, bool windowsDark, bool dark) =>
        Assert.Equal(dark, LauncherSettings.IsDark(mode, windowsDark));

    [Fact]
    public void Theme_Cycles() =>
        Assert.Equal(ThemeMode.System, LauncherSettings.Next(LauncherSettings.Next(LauncherSettings.Next(ThemeMode.System))));

    [Fact]
    public void Notices_AnnounceEachUpdateOnce()
    {
        var n = new UpdateNotices();
        var v1 = AppVersion.TryParse("0.1.140")!;
        Assert.Equal(["Demo 0.1.140"], n.New([("Demo", v1)]));
        Assert.Empty(n.New([("Demo", v1)]));
        Assert.Equal(["Demo 0.1.141"], n.New([("Demo", AppVersion.TryParse("0.1.141")!), ("Demo", v1)]));
    }
}
