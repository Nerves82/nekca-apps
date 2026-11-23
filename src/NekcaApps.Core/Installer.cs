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

    /// <summary>Downloads and unpacks a release (if it isn't installed yet). Doesn't change the version in use.</summary>
    public async Task InstallAsync(AppEntry app, Release release, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var install = state.For(app.Id);
        var version = release.Version.ToString();
        if (install.Installed.TryGetValue(version, out var exe) && File.Exists(Path.Combine(layout.VersionFolder(app.Id, version), exe))) return;

        Directory.CreateDirectory(layout.DownloadFolder);
        var zip = Path.Combine(layout.DownloadFolder, $"{app.Id}-{version}-{Guid.NewGuid():N}.zip");
        var target = layout.VersionFolder(app.Id, version);
        var partial = target + ".partial";
        try
        {
            await DownloadAsync(release.Asset, zip, progress, ct);
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            try { ZipFile.ExtractToDirectory(zip, partial); }
            catch (InvalidDataException e) { throw new InstallException("The download isn't a usable zip file. Try again.", e); }
            var found = Directory.EnumerateFiles(partial, app.Exe, SearchOption.AllDirectories)
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar)).FirstOrDefault()
                ?? throw new InstallException($"{app.Exe} isn't in the download for version {version}.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(partial, target);
            install.Installed[version] = Path.GetRelativePath(partial, found);
            state.Save(layout);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InstallException("Windows couldn't write the app's files: " + e.Message, e);
        }
        finally
        {
            TryDelete(zip);
            if (Directory.Exists(partial)) try { Directory.Delete(partial, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
        var target = layout.VersionFolder(app.Id, version);
        var partial = target + ".partial";
        try
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            CopyFolder(sourceFolder, partial);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(partial, target);
            install.Installed[version] = app.Exe;
            state.Save(layout);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InstallException("Windows couldn't copy the files: " + e.Message, e);
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
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
                // rename first: if a file is open the rename fails and nothing is half-deleted
                var doomed = folder + ".removing-" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(folder, doomed);
                Directory.Delete(doomed, recursive: true);
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
