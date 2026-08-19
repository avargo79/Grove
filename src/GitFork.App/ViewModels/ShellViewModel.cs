using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitFork.Core;

namespace GitFork.App.ViewModels;

/// <summary>A repository in the recent list, with its folder name separated from its path.</summary>
public sealed record RecentRepository(string Path)
{
    public string Name => System.IO.Path.GetFileName(
        Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/'));

    public bool Exists => Directory.Exists(Path);
}

/// <summary>
/// The application shell: several repositories open at once as tabs, the recent list, and the
/// settings they all share.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsStore _store;

    public ShellViewModel(SettingsStore? store = null)
    {
        _store = store ?? new SettingsStore();
        Settings = _store.Load();
        RefreshRecent();
    }

    /// <summary>Set by the view; opens a native folder picker.</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>Set by the view; called for each newly opened repository so it gets its dialogs.</summary>
    public Action<MainViewModel>? ConfigureRepository { get; set; }

    /// <summary>Set to false in tests, where a watcher would race the assertions.</summary>
    public bool WatchForChanges { get; init; } = true;

    public ObservableCollection<MainViewModel> Repositories { get; } = [];

    public ObservableCollection<RecentRepository> Recent { get; } = [];

    [ObservableProperty]
    public partial MainViewModel? SelectedRepository { get; set; }

    [ObservableProperty]
    public partial AppSettings Settings { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; } = "Open a repository to get started.";

    public bool HasRepositories => Repositories.Count > 0;

    public bool HasNoRepositories => Repositories.Count == 0;

    /// <summary>Exposed for tests, which need to await the fire-and-forget command bodies.</summary>
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;

    // ------------------------------------------------------------ opening

    [RelayCommand]
    private Task OpenRepositoryAsync()
    {
        PendingOperation = PickAndOpenAsync();
        return PendingOperation;
    }

    private async Task PickAndOpenAsync()
    {
        if (PickFolderAsync is null)
            return;

        var path = await PickFolderAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(path))
            await OpenAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task OpenRecentAsync(RecentRepository? recent)
    {
        PendingOperation = recent is null ? Task.CompletedTask : OpenAsync(recent.Path);
        return PendingOperation;
    }

    /// <summary>
    /// Opens a repository as a new tab, or brings its existing tab forward. Opening the same
    /// repository twice would give two views of one thing, which only invites confusion.
    /// </summary>
    public async Task<MainViewModel?> OpenAsync(string path)
    {
        var root = await GitCommandRunner.DiscoverRepositoryRootAsync(path).ConfigureAwait(true);
        if (root is null)
        {
            StatusMessage = $"'{path}' is not inside a git repository.";
            return null;
        }

        if (Repositories.FirstOrDefault(r => r.RepositoryPath == root) is { } existing)
        {
            SelectedRepository = existing;
            return existing;
        }

        var repository = new MainViewModel { WatchForChanges = WatchForChanges };
        ConfigureRepository?.Invoke(repository);

        Repositories.Add(repository);
        SelectedRepository = repository;
        NotifyRepositoriesChanged();

        await repository.LoadRepositoryAsync(root).ConfigureAwait(true);

        // Only remember it once it actually opened.
        Settings = Settings.WithRecentRepository(root);
        Persist();
        RefreshRecent();

        StatusMessage = null;
        return repository;
    }

    [RelayCommand]
    private void CloseRepository(MainViewModel? repository)
    {
        if (repository is null)
            return;

        var index = Repositories.IndexOf(repository);
        Repositories.Remove(repository);
        repository.Dispose();

        // Land on a neighbour rather than nothing, the way tabs normally behave.
        SelectedRepository = Repositories.Count == 0
            ? null
            : Repositories[Math.Min(index, Repositories.Count - 1)];

        NotifyRepositoriesChanged();
    }

    [RelayCommand]
    private void RemoveRecent(RecentRepository? recent)
    {
        if (recent is null)
            return;

        Settings = Settings.WithoutRecentRepository(recent.Path);
        Persist();
        RefreshRecent();
    }

    // ----------------------------------------------------------- settings

    /// <summary>Set by the view; switches the application palette.</summary>
    public Action<AppTheme>? ApplyTheme { get; set; }

    /// <summary>Applies changed settings everywhere and writes them out.</summary>
    public void ApplySettings(AppSettings settings)
    {
        var themeChanged = settings.Theme != Settings.Theme;

        Settings = settings;
        Persist();

        if (themeChanged)
            ApplyTheme?.Invoke(settings.Theme);

        foreach (var repository in Repositories)
            repository.ApplySettings(settings);
    }

    private void Persist() => _store.Save(Settings);

    private void RefreshRecent()
    {
        Recent.Clear();
        foreach (var path in Settings.RecentRepositories)
            Recent.Add(new RecentRepository(path));
    }

    private void NotifyRepositoriesChanged()
    {
        OnPropertyChanged(nameof(HasRepositories));
        OnPropertyChanged(nameof(HasNoRepositories));
    }

    public void Dispose()
    {
        foreach (var repository in Repositories)
            repository.Dispose();
        Repositories.Clear();
    }
}
