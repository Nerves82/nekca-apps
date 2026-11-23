namespace NekcaApps.Core.Tests;

public class LauncherSettingsTests
{
    [Fact]
    public void Settings_SaveAndLoad_WithSafeDefaults()
    {
        var layout = new InstallLayout(Fakes.TempFolder());
        var fresh = LauncherSettings.Load(layout);
        Assert.Equal(AppTheme.System, fresh.Theme);
        Assert.True(fresh.CloseToTray);
        Assert.False(fresh.StartWithWindows);

        fresh.Theme = AppTheme.Dark;
        fresh.StartWithWindows = true;
        fresh.Save(layout);
        var again = LauncherSettings.Load(layout);
        Assert.Equal(AppTheme.Dark, again.Theme);
        Assert.True(again.StartWithWindows);
        Assert.Contains("\"Dark\"", File.ReadAllText(Path.Combine(layout.Root, "settings.json")));

        File.WriteAllText(Path.Combine(layout.Root, "settings.json"), "{ not json");
        Assert.Equal(AppTheme.System, LauncherSettings.Load(layout).Theme);
    }

    [Theory]
    [InlineData(AppTheme.System, false, false)]
    [InlineData(AppTheme.System, true, true)]
    [InlineData(AppTheme.Light, true, false)]
    [InlineData(AppTheme.Dark, false, true)]
    public void Theme_FollowsWindowsOnlyOnSystem(AppTheme mode, bool windowsDark, bool dark) =>
        Assert.Equal(dark, LauncherSettings.IsDark(mode, windowsDark));

    [Fact]
    public void Theme_Cycles() =>
        Assert.Equal(AppTheme.System, LauncherSettings.Next(LauncherSettings.Next(LauncherSettings.Next(AppTheme.System))));

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
