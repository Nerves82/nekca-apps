using System.IO;
using NekcaApps.Core;

namespace NekcaApps;

/// <summary>
/// Per-user shortcuts (no admin): Start Menu\Programs\NEKCA Apps\&lt;name&gt;.lnk, and optionally one on the desktop.
/// Made with the Windows Script Host shell object, which every Windows install has.
/// </summary>
public sealed class WindowsShortcuts : IShortcuts
{
    private static string StartFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NEKCA Apps");
    private static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public void Set(string name, string exePath, bool desktop)
    {
        Directory.CreateDirectory(StartFolder);
        Write(Path.Combine(StartFolder, name + ".lnk"), exePath);
        var onDesktop = Path.Combine(Desktop, name + ".lnk");
        if (desktop) Write(onDesktop, exePath);
        else TryDelete(onDesktop);
    }

    public void Remove(string name)
    {
        TryDelete(Path.Combine(StartFolder, name + ".lnk"));
        TryDelete(Path.Combine(Desktop, name + ".lnk"));
        // the Start Menu folder goes once it's empty
        try { if (Directory.Exists(StartFolder) && !Directory.EnumerateFileSystemEntries(StartFolder).Any()) Directory.Delete(StartFolder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Write(string lnk, string exe)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InstallException("Windows can't make shortcuts on this computer.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic s = shell.CreateShortcut(lnk);
            s.TargetPath = exe;
            s.WorkingDirectory = Path.GetDirectoryName(exe);
            s.IconLocation = exe + ",0";
            s.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
