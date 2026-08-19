using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitGui.Core;

namespace GitGui.App.ViewModels;

/// <summary>One reflog entry as shown in the browser.</summary>
public sealed class ReflogEntryViewModel(ReflogEntry entry, bool isUnreachable)
{
    public ReflogEntry Entry { get; } = entry;

    /// <summary>True when no branch or tag can reach this commit any more.</summary>
    public bool IsUnreachable { get; } = isUnreachable;

    public string Selector => Entry.Selector;
    public string ShortSha => Entry.ShortSha;
    public string Action => Entry.Action;
    public string Subject => Entry.Subject;
    public string DateDisplay => RelativeTime.Format(Entry.Date);
    public bool IsPotentiallyDestructive => Entry.IsPotentiallyDestructive;

    public string ToolTip => IsUnreachable
        ? $"{Entry.Sha} — no branch reaches this commit any more"
        : Entry.Sha;
}

/// <summary>
/// The reflog browser. Its reason to exist is recovering work: an orphaned commit can be checked
/// out, branched from, or reset to.
/// </summary>
public sealed partial class ReflogViewModel(GitRepository repository) : ViewModelBase
{
    public event EventHandler? RepositoryChanged;

    /// <summary>Set by the view; confirms before anything destructive.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Set by the view; asks for a branch name.</summary>
    public Func<PromptRequest, Task<string?>>? PromptAsync { get; set; }

    public ObservableCollection<ReflogEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    public partial ReflogEntryViewModel? SelectedEntry { get; set; }

    [ObservableProperty]
    public partial bool ShowOnlyUnreachable { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>Exposed for tests, which need to await the fire-and-forget command bodies.</summary>
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;

    /// <summary>The in-flight reload, so callers can await a filter change deterministically.</summary>
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var all = await repository.Reflog.GetEntriesAsync(ct: ct).ConfigureAwait(true);
        var unreachable = await repository.Reflog.GetUnreachableEntriesAsync(ct: ct).ConfigureAwait(true);
        var unreachableShas = unreachable.Select(e => e.Sha).ToHashSet(StringComparer.Ordinal);

        var previous = SelectedEntry?.Entry.Selector;
        Entries.Clear();

        foreach (var entry in all)
        {
            var isUnreachable = unreachableShas.Contains(entry.Sha);
            if (ShowOnlyUnreachable && !isUnreachable)
                continue;

            Entries.Add(new ReflogEntryViewModel(entry, isUnreachable));
        }

        StatusText = unreachableShas.Count == 0
            ? $"{all.Count:N0} entries · nothing is unreachable"
            : $"{all.Count:N0} entries · {unreachableShas.Count:N0} unreachable";

        SelectedEntry = Entries.FirstOrDefault(e => e.Selector == previous) ?? Entries.FirstOrDefault();
    }

    partial void OnShowOnlyUnreachableChanged(bool value) => PendingLoad = LoadAsync();

    // ---------------------------------------------------------- recovering

    [RelayCommand]
    private Task CheckoutAsync(ReflogEntryViewModel? entry) => RunAsync(entry, e =>
        repository.Refs.CheckoutCommitAsync(e.Entry.Sha));

    [RelayCommand]
    private Task CreateBranchAsync(ReflogEntryViewModel? entry) => RunAsync(entry, async e =>
    {
        var prompt = PromptAsync;
        if (prompt is null)
            return OperationResult.Cancelled;

        var name = await prompt(new PromptRequest(
            "Recover onto a branch", $"Name for a branch at {e.ShortSha}:")).ConfigureAwait(true);

        return string.IsNullOrWhiteSpace(name)
            ? OperationResult.Cancelled
            : await repository.Refs.CreateBranchAsync(name.Trim(), e.Entry.Sha).ConfigureAwait(true);
    });

    [RelayCommand]
    private Task ResetToAsync(ReflogEntryViewModel? entry) => RunAsync(entry, async e =>
    {
        var confirm = ConfirmAsync;
        if (confirm is null ||
            !await confirm($"Reset the current branch to {e.ShortSha} and discard all uncommitted changes? " +
                           "This cannot be undone.").ConfigureAwait(true))
            return OperationResult.Cancelled;

        return await repository.History.ResetAsync(e.Entry.Sha, ResetMode.Hard).ConfigureAwait(true);
    });

    private Task RunAsync(ReflogEntryViewModel? entry, Func<ReflogEntryViewModel, Task<OperationResult>> operation)
    {
        PendingOperation = ExecuteAsync(entry, operation);
        return PendingOperation;
    }

    private async Task ExecuteAsync(
        ReflogEntryViewModel? entry, Func<ReflogEntryViewModel, Task<OperationResult>> operation)
    {
        if (entry is null)
            return;

        try
        {
            var result = await operation(entry).ConfigureAwait(true);
            if (result.Outcome != OperationOutcome.Cancelled)
                StatusText = result.Message;
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            await LoadAsync().ConfigureAwait(true);
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
