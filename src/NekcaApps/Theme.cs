using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using NekcaApps.Core;

namespace NekcaApps;

/// <summary>Light and dark colours (the brushes in App.xaml), and the window title bar to match.</summary>
public static class Theme
{
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["Page"] = ("#F3F5F3", "#121815"),
        ["Card"] = ("#FFFFFF", "#1C2420"),
        ["Green"] = ("#1C5630", "#8FD4A3"),
        ["PrimaryBg"] = ("#1C5630", "#2E7D4A"),
        ["OnPrimary"] = ("#FFFFFF", "#FFFFFF"),
        ["GreenSoft"] = ("#E6F0E8", "#22352A"),
        ["Ink"] = ("#1F2A24", "#E4EBE6"),
        ["Muted"] = ("#5F6B64", "#9AA9A0"),
        ["Line"] = ("#D5DDD7", "#33403A"),
        ["Warn"] = ("#8A5A00", "#F2B660"),
        ["WarnSoft"] = ("#FFF4D6", "#3A2C12"),
    };

    public static bool IsDark { get; private set; }

    /// <summary>Windows' "Choose your app mode" setting (Settings › Personalization › Colors).</summary>
    public static bool WindowsUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    public static void Apply(AppTheme mode)
    {
        IsDark = LauncherSettings.IsDark(mode, WindowsUsesDark());
        var res = Application.Current.Resources;
        foreach (var (key, (light, dark)) in Palette)
            res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? dark : light));
        foreach (Window w in Application.Current.Windows) TitleBar(w);
    }

    /// <summary>Dark title bar on Windows 10 (20H1+) and 11; ignored elsewhere.</summary>
    public static void TitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = IsDark ? 1 : 0;
        try { _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); } catch (Exception) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
