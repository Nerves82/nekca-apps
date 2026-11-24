using System.IO.Compression;
using System.Security.Cryptography;

namespace NekcaApps.Core;

/// <summary>Start Menu and desktop shortcuts (Windows-only, so the window project provides them).</summary>
public interface IShortcuts
{
    /// <summary>Creates or repoints the app's shortcuts to <paramref name="exePath"/>.</summary>
    void Set(string name, string exePath, bool desktop);
    void Remove(string name);
}

/// <summary>What's left to delete after the launcher exits, and apps that couldn't be removed because they were open.</summary>
public sealed record LauncherRemoval(IReadOnlyList<string> DeleteAfterExit, IReadOnlyList<string> AppsStillOpen);

/// <summary>Bytes downloaded so far, and the total when known.</summary>
public readonly record struct DownloadProgress(long Done, long? Total)
{
    public double? Fraction => Total is > 0 ? Math.Min(1, (double)Done / Total.Value) : null;
}

/// <summary>Something that went wrong that the person can act on; the message is shown as is.</summary>
public sealed class InstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Installs versions side by side (one folder each), switches which one is in use, and removes old ones.
/// A version only appears in <see cref="InstallState"/> once its folder is complete.
/// </summary>
public sealed class Installer(InstallLayout layout, InstallState state, HttpClient http, IShortcuts shortcuts)
{
    public InstallLayout Layout => layout;
    public InstallState State => state;

    /// <summary>
    /// Downloads and unpacks a release (if it isn't installed yet). Doesn't change the version in use.
    /// Unpacks straight into the version's folder; the version only counts as installed (state.json) once every file
    /// is there. No folder is renamed after writing: Windows refuses that while the virus scanner is still reading
    /// the new files, which it does right after they're written.
    /// </summary>
    public async Task InstallAsync(AppEntry app, Release release, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var install = state.For(app.Id);
        var version = release.Version.ToString();
        if (install.Installed.TryGetValue(version, out var exe) && File.Exists(Path.Combine(layout.VersionFolder(app.Id, version), exe))) return;

        CleanUpLeftovers(app.Id);
        Directory.CreateDirectory(layout.DownloadFolder);
        var zip = Path.Combine(layout.DownloadFolder, $"{app.Id}-{version}-{Guid.NewGuid():N}.zip");
        var target = layout.VersionFolder(app.Id, version);
        var done = false;
        try
        {
            await DownloadAsync(release.Asset, zip, progress, ct);
            var relative = await Task.Run(() =>
            {
                StartFresh(target);
                try { IoRetry.Run(() => ZipFile.ExtractToDirectory(zip, target, overwriteFiles: true)); }
                catch (InvalidDataException e) { throw new InstallException("The download isn't a usable zip file. Try again.", e); }
                var found = Directory.EnumerateFiles(target, app.Exe, SearchOption.AllDirectories)
                    .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar)).FirstOrDefault()
                    ?? throw new InstallException($"{app.Exe} isn't in the download for version {version}.");
                return Path.GetRelativePath(target, found);
            }, ct);
            install.Installed[version] = relative;
            state.Save(layout);
            done = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(Locked("write the app's files", e), e);
        }
        finally
        {
            TryDelete(zip);
            if (!done) TryDeleteFolder(target);
        }
    }

    /// <summary>
    /// Copies a version that's already unpacked somewhere (the launcher run from a downloaded zip) into the install
    /// folder. Does nothing when that version is installed.
    /// </summary>
    public void InstallFromFolder(AppEntry app, string version, string sourceFolder)
    {
        var install = state.For(app.Id);
        if (install.Installed.ContainsKey(version) && ExePath(app.Id, version) is { } have && File.Exists(have)) return;
        if (!File.Exists(Path.Combine(sourceFolder, app.Exe))) throw new InstallException($"{app.Exe} isn't in {sourceFolder}.");
        CleanUpLeftovers(app.Id);
        var target = layout.VersionFolder(app.Id, version);
        try
        {
            StartFresh(target);
            CopyFolder(sourceFolder, target);
            install.Installed[version] = app.Exe;
            state.Save(layout);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder(target);
            throw new InstallException(Locked("copy the files", e), e);
        }
    }

    /// <summary>
    /// Installs a version that is one self-contained program file (the setup download, NekcaAppsSetup-x.exe): copies
    /// just that file, named <see cref="AppEntry.Exe"/>. Does nothing when that version is installed.
    /// </summary>
    public void InstallFromFile(AppEntry app, string version, string exeFile)
    {
        var install = state.For(app.Id);
        if (install.Installed.ContainsKey(version) && ExePath(app.Id, version) is { } have && File.Exists(have)) return;
        if (!File.Exists(exeFile)) throw new InstallException($"{exeFile} isn't there any more.");
        CleanUpLeftovers(app.Id);
        var target = layout.VersionFolder(app.Id, version);
        var dest = Path.Combine(target, app.Exe);
        if (SameFile(exeFile, dest))
        {
            // already there (another setup run copied it, and it may be running): just record it
            install.Installed[version] = app.Exe;
            state.Save(layout);
            return;
        }
        try
        {
            StartFresh(target);
            IoRetry.Run(() => File.Copy(exeFile, dest, overwrite: true));
            install.Installed[version] = app.Exe;
            state.Save(layout);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder(target);
            throw new InstallException(Locked("copy the program", e), e);
        }
    }

    /// <summary>True when both files exist with the same contents.</summary>
    private static bool SameFile(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return System.Security.Cryptography.SHA256.HashData(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Removes what an interrupted or older install left behind in the app's folder: "x.partial" and "x.removing-…"
    /// folders. (Version folders are never removed here, even unlisted ones, in case state.json was damaged.)
    /// Anything still locked is left for next time.
    /// </summary>
    public void CleanUpLeftovers(string appId)
    {
        var folder = layout.AppFolder(appId);
        if (!Directory.Exists(folder)) return;
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(dir);
            if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || name.Contains(".removing-", StringComparison.OrdinalIgnoreCase))
                TryDeleteFolder(dir, attempts: 1);
        }
    }

    /// <summary>An empty folder to install into (an earlier failed try's files are cleared, or overwritten if locked).</summary>
    private static void StartFresh(string folder)
    {
        if (Directory.Exists(folder))
        {
            try { IoRetry.Run(() => Directory.Delete(folder, recursive: true)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* overwrite what's there instead */ }
        }
        IoRetry.Run(() => Directory.CreateDirectory(folder));
    }

    private static string Locked(string what, Exception e) =>
        $"Windows couldn't {what}: another program, usually the virus scanner checking the new files, was using them. " +
        $"Wait a minute and try again. ({e.Message})";

    private static void TryDeleteFolder(string folder, int attempts = IoRetry.DefaultAttempts)
    {
        try { if (Directory.Exists(folder)) IoRetry.Run(() => Directory.Delete(folder, recursive: true), attempts); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) IoRetry.Run(() => File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyFolder(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>
    /// Makes an installed version the one in use: shortcuts point to it, and older versions are removed except the
    /// one used just before. Returns versions that couldn't be removed (still open).
    /// </summary>
    public IReadOnlyList<string> Activate(AppEntry app, string version)
    {
        var install = state.For(app.Id);
        if (!install.Installed.ContainsKey(version)) throw new InstallException($"Version {version} isn't installed.");
        var previous = install.Active;
        install.Active = version;
        state.Save(layout);
        shortcuts.Set(app.Name, ExePath(app.Id, version)!, install.DesktopShortcut);
        var stuck = new List<string>();
        foreach (var old in AppStatusRules.ToClean(install, previous))
            if (!TryRemove(app.Id, old)) stuck.Add(old);
        return stuck;
    }

    /// <summary>Removes an installed version that isn't in use. False when its files are open.</summary>
    public bool Remove(AppEntry app, string version)
    {
        if (state.For(app.Id).Active == version) throw new InstallException("That's the version in use. Switch to another version first.");
        return TryRemove(app.Id, version);
    }

    /// <summary>Removes the app completely: every version and its shortcuts (not the app's own data or settings).</summary>
    public bool Uninstall(AppEntry app)
    {
        var install = state.For(app.Id);
        install.Active = null;
        state.Save(layout);
        shortcuts.Remove(app.Name);
        var ok = install.Installed.Keys.ToList().Aggregate(true, (all, v) => TryRemove(app.Id, v) && all);
        if (ok) { state.Apps.Remove(app.Id); state.Save(layout); }
        return ok;
    }

    /// <summary>
    /// Removes NEKCA Apps itself: its shortcuts and its entry in state.json, and with <paramref name="removeApps"/>
    /// every app it installed. The running launcher's files can't be deleted while it runs, so this returns the
    /// folders and files to delete once it has exited (all inside the install folder), and the apps that were open.
    /// </summary>
    public LauncherRemoval UninstallLauncher(IEnumerable<AppEntry> apps, bool removeApps)
    {
        var stillOpen = new List<string>();
        if (removeApps)
            foreach (var app in apps.Where(a => a.Id != Launcher.Entry.Id && state.Apps.ContainsKey(a.Id)))
                if (!Uninstall(app)) stillOpen.Add(app.Name);
        shortcuts.Remove(Launcher.Entry.Name);
        state.Apps.Remove(Launcher.Entry.Id);
        state.Save(layout);

        // everything gone: the whole folder; otherwise just the launcher (the apps' versions and state.json stay)
        var nothingLeft = state.Apps.Values.All(a => a.Installed.Count == 0);
        IReadOnlyList<string> delete = nothingLeft
            ? [layout.Root]
            : [layout.AppFolder(Launcher.Entry.Id), layout.CacheFolder, layout.DownloadFolder, Path.Combine(layout.Root, "settings.json")];
        return new(delete, stillOpen);
    }

    public void SetDesktopShortcut(AppEntry app, bool on)
    {
        var install = state.For(app.Id);
        install.DesktopShortcut = on;
        state.Save(layout);
        if (install.Active is { } v && ExePath(app.Id, v) is { } exe) shortcuts.Set(app.Name, exe, on);
    }

    /// <summary>The program of an installed version, or null.</summary>
    public string? ExePath(string appId, string version) =>
        state.For(appId).Installed.TryGetValue(version, out var rel) ? Path.Combine(layout.VersionFolder(appId, version), rel) : null;

    private bool TryRemove(string appId, string version)
    {
        var folder = layout.VersionFolder(appId, version);
        try
        {
            if (Directory.Exists(folder))
            {
                // rename first: if the program is open the rename fails and nothing is half-deleted
                // (retried, since the virus scanner can hold files for a moment too)
                var doomed = folder + ".removing-" + Guid.NewGuid().ToString("N")[..6];
                IoRetry.Run(() => Directory.Move(folder, doomed), attempts: 4);
                TryDeleteFolder(doomed); // anything left is cleaned up next time
            }
            state.For(appId).Installed.Remove(version);
            state.Save(layout);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task DownloadAsync(ReleaseAsset asset, string file, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) throw new InstallException($"The download failed (GitHub answered {(int)res.StatusCode}). Try again later.");
            var total = res.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
            await using var input = await res.Content.ReadAsStreamAsync(ct);
            await using (var output = File.Create(file))
            {
                var buffer = new byte[81920];
                long done = 0; int read;
                var lastReport = 0L;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (done - lastReport > 256 * 1024 || done == total) { progress?.Report(new(done, total)); lastReport = done; }
                }
            }
            if (asset.Sha256 is { Length: 64 } expected)
            {
                await using var check = File.OpenRead(file);
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct));
                if (actual != expected) throw new InstallException("The download was damaged on the way (its checksum doesn't match). Try again.");
            }
        }
        catch (HttpRequestException e) { throw new InstallException("Couldn't download from GitHub (offline?). " + e.Message, e); }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
