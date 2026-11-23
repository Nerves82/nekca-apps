using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using NekcaApps.Core;

namespace NekcaApps;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    protected void RaiseAll() => PropertyChanged?.Invoke(this, new(string.Empty));
}

/// <summary>A button's action.</summary>
public sealed class Command(Func<Task> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public async void Execute(object? parameter) => await run();
}

/// <summary>One app on the screen.</summary>
public sealed class AppCard(AppEntry app) : Observable
{
    public AppEntry App { get; } = app;
    public string Name => App.Name;
    public string? Description => App.Description;

    public IReadOnlyList<Release> Releases { get; private set; } = [];
    public string? ReleasesProblem { get; private set; }
    public AppInstall Install { get; private set; } = new();
    public ObservableCollection<VersionRow> Versions { get; } = [];

    public AppStatus Status => AppStatusRules.Status(Install, Releases);
    public Release? Latest => GitHubReleases.Latest(Releases);
    public bool IsInstalled => Install.Active is not null;
    public bool CanInstall => !IsInstalled && Latest is not null && !Busy;
    public bool CanUpdate => Status == AppStatus.UpdateAvailable && !Busy;
    public bool CanOpen => IsInstalled && !Busy;
    public string UpdateText => Latest is { } l ? $"Update to {l.Version}" : "Update";

    public string StatusText => Status switch
    {
        AppStatus.NotInstalled => Latest is { } l ? $"Not installed · newest version {l.Version} ({l.Published.LocalDateTime:MMM d, yyyy})" : "Not installed",
        AppStatus.UpToDate => $"Version {Install.Active} · up to date",
        AppStatus.UpdateAvailable => $"Version {Install.Active} · version {Latest!.Version} is available",
        AppStatus.NewerThanLatest => $"Version {Install.Active} (newer than the latest release)",
        _ => $"Version {Install.Active}",
    } + (ReleasesProblem is { } p ? $"  ({p})" : "");

    public bool UpdateAvailable => Status == AppStatus.UpdateAvailable;

    public bool DesktopShortcut
    {
        get => Install.DesktopShortcut;
        set { if (value != Install.DesktopShortcut) DesktopShortcutChanged?.Invoke(this, value); }
    }

    public event Action<AppCard, bool>? DesktopShortcutChanged;

    private bool busy;
    public bool Busy { get => busy; set { busy = value; RaiseAll(); } }

    private string? progressText;
    public string? ProgressText { get => progressText; set { progressText = value; Raise(); Raise(nameof(ShowProgress)); } }

    private double? progress;
    public double? Progress { get => progress; set { progress = value; Raise(); Raise(nameof(ProgressValue)); Raise(nameof(ProgressUnknown)); } }
    public double ProgressValue => (progress ?? 0) * 100;
    public bool ProgressUnknown => progress is null;
    public bool ShowProgress => progressText is not null;

    private string? message;
    /// <summary>The last result or problem, shown under the buttons.</summary>
    public string? Message { get => message; set { message = value; Raise(); Raise(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(message);

    public Command? InstallCommand { get; set; }
    public Command? UpdateCommand { get; set; }
    public Command? OpenCommand { get; set; }
    public Command? UninstallCommand { get; set; }
    public Func<VersionRow, Task>? UseVersion { get; set; }
    public Func<VersionRow, Task>? RemoveVersion { get; set; }

    public void Refresh(IReadOnlyList<Release> releases, string? problem, AppInstall install)
    {
        Releases = releases;
        ReleasesProblem = problem;
        Install = install;
        Versions.Clear();
        // every release, plus installed versions that aren't (or are no longer) on GitHub
        var rows = releases.Select(r => new VersionRow(this, r.Version.ToString(), r)).ToList();
        foreach (var v in install.Installed.Keys.Where(v => rows.All(r => r.Version != v)))
            rows.Add(new VersionRow(this, v, null));
        foreach (var r in rows.OrderByDescending(r => AppVersion.TryParse(r.Version)))
            Versions.Add(r);
        RaiseAll();
    }
}

/// <summary>One version in an app's "All versions" list.</summary>
public sealed class VersionRow(AppCard card, string version, Release? release)
{
    public string Version { get; } = version;
    public Release? Release { get; } = release;
    public string Date => Release is { } r ? r.Published.LocalDateTime.ToString("MMM d, yyyy") : "";
    public bool InUse => card.Install.Active == Version;
    public bool Installed => card.Install.Installed.ContainsKey(Version);
    public string Tag => InUse ? "in use" : Installed ? "installed" : Release?.Prerelease == true ? "pre-release" : "";
    public bool HasTag => Tag.Length > 0;
    public string Changes => Release is null ? "No longer listed on GitHub."
        : Release.Changes.Count == 0 ? Release.Title : string.Join("\n", Release.Changes.Take(6).Select(c => "• " + c))
          + (Release.Changes.Count > 6 ? $"\n• …and {Release.Changes.Count - 6} more" : "");
    public bool CanUse => !InUse && (Installed || Release is not null);
    public string UseText => Installed ? "Use this version" : "Install and use";
    public bool CanRemove => Installed && !InUse;
    public Command UseCommand => new(() => card.UseVersion?.Invoke(this) ?? Task.CompletedTask);
    public Command RemoveCommand => new(() => card.RemoveVersion?.Invoke(this) ?? Task.CompletedTask);
}
