using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.App.ViewModels;

/// <summary>A submodule as shown in the panel.</summary>
public sealed class SubmoduleViewModel(Submodule submodule)
{
    public Submodule Submodule { get; } = submodule;

    public string Path => Submodule.Path;
    public string ShortSha => Submodule.ShortSha;
    public string Describe => Submodule.Describe ?? string.Empty;

    public string StateDisplay => Submodule.State switch
    {
        SubmoduleState.NotInitialised => "not initialised",
        SubmoduleState.OutOfDate => "different commit checked out",
        SubmoduleState.Conflicted => "conflicted",
        _ => "up to date",
    };

    public bool NeedsAttention => Submodule.State != SubmoduleState.UpToDate;
}

/// <summary>
/// Submodules and git-lfs. Both are absent from most repositories, so the panel says so plainly
/// rather than showing empty lists that look like a failure.
/// </summary>
public sealed partial class IntegrationsViewModel(GitRepository repository) : ViewModelBase
{
    public event EventHandler? RepositoryChanged;

    public ObservableCollection<SubmoduleViewModel> Submodules { get; } = [];
    public ObservableCollection<LfsFile> LfsFiles { get; } = [];
    public ObservableCollection<LfsLock> LfsLocks { get; } = [];

    [ObservableProperty]
    public partial bool IsLfsAvailable { get; set; }

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    public partial bool IsUpdating { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    public bool HasSubmodules => Submodules.Count > 0;
    public bool HasNoSubmodules => Submodules.Count == 0;
    public bool HasLfsFiles => LfsFiles.Count > 0;
    public bool HasLfsLocks => LfsLocks.Count > 0;

    /// <summary>Exposed for tests, which need to await the fire-and-forget command body.</summary>
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Submodules.Clear();
        foreach (var submodule in await repository.Integrations.GetSubmodulesAsync(ct).ConfigureAwait(true))
            Submodules.Add(new SubmoduleViewModel(submodule));

        IsLfsAvailable = await repository.Integrations.IsLfsEnabledAsync(ct).ConfigureAwait(true);

        LfsFiles.Clear();
        LfsLocks.Clear();

        if (IsLfsAvailable)
        {
            foreach (var file in await repository.Integrations.GetLfsFilesAsync(ct).ConfigureAwait(true))
                LfsFiles.Add(file);
            foreach (var held in await repository.Integrations.GetLfsLocksAsync(ct).ConfigureAwait(true))
                LfsLocks.Add(held);
        }

        NotifyCounts();

        StatusText = Submodules.Count switch
        {
            0 => "No submodules in this repository.",
            1 => "1 submodule",
            var n => $"{n} submodules",
        };
    }

    [RelayCommand]
    private Task UpdateSubmodulesAsync()
    {
        PendingOperation = RunUpdateAsync();
        return PendingOperation;
    }

    private async Task RunUpdateAsync()
    {
        IsUpdating = true;
        ProgressText = null;

        try
        {
            var progress = new Progress<string>(line => ProgressText = line);
            var result = await repository.Integrations
                .UpdateSubmodulesAsync(progress)
                .ConfigureAwait(true);

            StatusText = result.Message;
            await LoadAsync().ConfigureAwait(true);
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsUpdating = false;
            ProgressText = null;
        }
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasSubmodules));
        OnPropertyChanged(nameof(HasNoSubmodules));
        OnPropertyChanged(nameof(HasLfsFiles));
        OnPropertyChanged(nameof(HasLfsLocks));
    }
}
