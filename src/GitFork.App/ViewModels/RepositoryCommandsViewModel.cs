using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitFork.Core;

namespace GitFork.App.ViewModels;

/// <summary>A request for a single line of text from the user.</summary>
public sealed record PromptRequest(string Title, string Message, string InitialValue = "");

/// <summary>
/// Every repository-level action the UI can invoke: fetch/pull/push, branch and tag lifecycle,
/// merge and rebase, cherry-pick and revert, reset, and the stash stack.
///
/// Kept out of <see cref="MainViewModel"/> so the shell stays about presentation and this stays
/// independently testable. Anything destructive is routed through <see cref="ConfirmAsync"/>.
/// </summary>
public sealed partial class RepositoryCommandsViewModel(GitRepository repository) : ViewModelBase, IDisposable
{
    private CancellationTokenSource? _networkCts;

    /// <summary>Raised after anything that changes the repository, so the shell can reload.</summary>
    public event EventHandler? RepositoryChanged;

    /// <summary>Set by the view: a modal yes/no. A null hook means "declined".</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Set by the view: a modal text prompt. Returns null when cancelled.</summary>
    public Func<PromptRequest, Task<string?>>? PromptAsync { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    /// <summary>Latest progress line from a running network operation.</summary>
    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    public partial bool IsNetworkOperationRunning { get; set; }

    [ObservableProperty]
    public partial RepositoryState State { get; set; } = RepositoryState.Clean;

    public bool IsOperationInProgress => State.IsInProgress;

    public string OperationDescription => State.Description;

    /// <summary>The banner's single line: what is in progress, and what is left to do about it.</summary>
    public string BannerText => $"{State.Description} — {ConflictSummary}";

    public string ConflictSummary => State.ConflictedPaths.Count switch
    {
        0 => "All conflicts resolved — continue when ready.",
        1 => "1 file still has conflicts.",
        var n => $"{n} files still have conflicts.",
    };

    /// <summary>Exposed for tests, which need to await the fire-and-forget command bodies.</summary>
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;

    public async Task RefreshStateAsync(CancellationToken ct = default)
    {
        State = await repository.History.GetStateAsync(ct).ConfigureAwait(true);

        // Content before visibility, so the banner measures with its final text.
        OnPropertyChanged(nameof(OperationDescription));
        OnPropertyChanged(nameof(ConflictSummary));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(IsOperationInProgress));
    }

    // ------------------------------------------------------------- network

    [RelayCommand]
    private Task FetchAsync() => RunNetworkAsync(
        (progress, ct) => repository.Remotes.FetchAsync(progress: progress, ct: ct));

    [RelayCommand]
    private Task PullAsync() => RunNetworkAsync(
        (progress, ct) => repository.Remotes.PullAsync(PullStrategy.Merge, progress, ct));

    [RelayCommand]
    private Task PullRebaseAsync() => RunNetworkAsync(
        (progress, ct) => repository.Remotes.PullAsync(PullStrategy.Rebase, progress, ct));

    [RelayCommand]
    private Task PushAsync() => RunAsync(async () =>
    {
        var result = await RunNetworkOperationAsync(
            (progress, ct) => repository.Remotes.PushAsync(progress: progress, ct: ct)).ConfigureAwait(true);

        // A branch with no upstream is the common first push; offer to create the tracking branch
        // rather than making the user find the flag.
        if (result.Succeeded || !result.Message.Contains("set upstream", StringComparison.OrdinalIgnoreCase))
            return result;

        var branch = await repository.GetCurrentBranchAsync().ConfigureAwait(true);
        if (branch is null)
            return result;

        var confirm = ConfirmAsync;
        if (confirm is null ||
            !await confirm($"'{branch}' does not exist on the remote yet. Create it and set it as the upstream?")
                .ConfigureAwait(true))
            return result;

        return await RunNetworkOperationAsync(
            (progress, ct) => repository.Remotes.PushAsync("origin", branch, setUpstream: true, progress: progress, ct: ct))
            .ConfigureAwait(true);
    });

    /// <summary>Force-with-lease rather than a plain force: it refuses if the remote moved unseen.</summary>
    [RelayCommand]
    private Task ForcePushAsync() => RunAsync(async () =>
    {
        var confirm = ConfirmAsync;
        if (confirm is null ||
            !await confirm("Force-push this branch? This rewrites what the remote is pointing at.")
                .ConfigureAwait(true))
            return OperationResult.Cancelled;

        return await RunNetworkOperationAsync(
            (progress, ct) => repository.Remotes.PushAsync(forceWithLease: true, progress: progress, ct: ct))
            .ConfigureAwait(true);
    });

    [RelayCommand]
    private void CancelNetworkOperation() => _networkCts?.Cancel();

    // ------------------------------------------------- conflict resolution

    [RelayCommand]
    private Task ContinueOperationAsync() => RunAsync(() => repository.History.ContinueAsync());

    [RelayCommand]
    private Task AbortOperationAsync() => RunAsync(async () =>
    {
        var confirm = ConfirmAsync;
        if (confirm is null ||
            !await confirm($"Abort the {State.Description.ToLowerInvariant()}? Any resolution work will be lost.")
                .ConfigureAwait(true))
            return OperationResult.Cancelled;

        return await repository.History.AbortAsync().ConfigureAwait(true);
    });

    // -------------------------------------------------------------- refs

    [RelayCommand]
    private Task CheckoutRefAsync(SidebarItemViewModel? item) => RunAsync(() => item?.Kind switch
    {
        RefKind.LocalBranch => repository.Refs.CheckoutBranchAsync(item.Ref.ShortName),
        RefKind.RemoteBranch => repository.Refs.CheckoutRemoteBranchAsync(item.Ref.ShortName),
        // A tag is not a branch, so checking one out detaches HEAD at its commit.
        RefKind.Tag => repository.Refs.CheckoutCommitAsync(item.Ref.TargetSha),
        _ => Task.FromResult(OperationResult.Cancelled),
    });

    [RelayCommand]
    private Task MergeRefAsync(SidebarItemViewModel? item) => RunAsync(
        () => item is null ? Task.FromResult(OperationResult.Cancelled) : repository.History.MergeAsync(item.Ref.ShortName));

    [RelayCommand]
    private Task RebaseOntoRefAsync(SidebarItemViewModel? item) => RunAsync(async () =>
    {
        if (item is null)
            return OperationResult.Cancelled;

        var confirm = ConfirmAsync;
        if (confirm is null ||
            !await confirm($"Rebase the current branch onto '{item.Ref.ShortName}'? This rewrites its commits.")
                .ConfigureAwait(true))
            return OperationResult.Cancelled;

        return await repository.History.RebaseAsync(item.Ref.ShortName).ConfigureAwait(true);
    });

    [RelayCommand]
    private Task CreateBranchAsync(string? startPoint) => RunAsync(async () =>
    {
        var name = await PromptForAsync("New branch", "Name for the new branch:").ConfigureAwait(true);
        return name is null ? OperationResult.Cancelled : await repository.Refs.CreateBranchAsync(name, startPoint).ConfigureAwait(true);
    });

    [RelayCommand]
    private Task RenameBranchAsync(SidebarItemViewModel? item) => RunAsync(async () =>
    {
        if (item is null)
            return OperationResult.Cancelled;

        var name = await PromptForAsync("Rename branch", $"New name for '{item.Ref.ShortName}':", item.Ref.ShortName)
            .ConfigureAwait(true);

        return name is null
            ? OperationResult.Cancelled
            : await repository.Refs.RenameBranchAsync(item.Ref.ShortName, name).ConfigureAwait(true);
    });

    [RelayCommand]
    private Task DeleteBranchAsync(SidebarItemViewModel? item) => RunAsync(async () =>
    {
        if (item is null)
            return OperationResult.Cancelled;

        var name = item.Ref.ShortName;
        var merged = await repository.Refs.IsBranchMergedAsync(name).ConfigureAwait(true);

        // Deleting an unmerged branch loses commits, so say so plainly instead of just failing.
        var message = merged
            ? $"Delete branch '{name}'?"
            : $"'{name}' has commits that exist nowhere else. Deleting it will lose them. Delete anyway?";

        var confirm = ConfirmAsync;
        if (confirm is null || !await confirm(message).ConfigureAwait(true))
            return OperationResult.Cancelled;

        return await repository.Refs.DeleteBranchAsync(name, force: !merged).ConfigureAwait(true);
    });

    [RelayCommand]
    private Task DeleteRemoteBranchAsync(SidebarItemViewModel? item) => RunAsync(async () =>
    {
        if (item?.Ref.RemoteName is not { } remote)
            return OperationResult.Cancelled;

        var branch = item.Ref.NameWithinRemote;
        var confirm = ConfirmAsync;
        if (confirm is null ||
            !await confirm($"Delete '{branch}' on '{remote}'? This affects everyone using this remote.")
                .ConfigureAwait(true))
            return OperationResult.Cancelled;

        return await RunNetworkOperationAsync(
            (progress, ct) => repository.Remotes.DeleteRemoteBranchAsync(remote, branch, progress, ct))
            .ConfigureAwait(true);
    });

    // --------------------------------------------------------------- tags

    [RelayCommand]
    private Task CreateTagAsync(string? target) => RunAsync(async () =>
    {
        var name = await PromptForAsync("New tag", "Name for the new tag:").ConfigureAwait(true);
        return name is null ? OperationResult.Cancelled : await repository.Refs.CreateTagAsync(name, target).ConfigureAwait(true);
    });

    [RelayCommand]
    private Task DeleteTagAsync(SidebarItemViewModel? item) => RunAsync(async () =>
    {
        if (item is null)
            return OperationResult.Cancelled;

        var confirm = ConfirmAsync;
        if (confirm is null || !await confirm($"Delete tag '{item.Ref.ShortName}'?").ConfigureAwait(true))
            return OperationResult.Cancelled;

        return await repository.Refs.DeleteTagAsync(item.Ref.ShortName).ConfigureAwait(true);
    });

    // ------------------------------------------------------------ commits

    [RelayCommand]
    private Task CheckoutCommitAsync(CommitRowViewModel? row) => RunAsync(
        () => row is null ? Task.FromResult(OperationResult.Cancelled) : repository.Refs.CheckoutCommitAsync(row.Sha));

    [RelayCommand]
    private Task CherryPickAsync(CommitRowViewModel? row) => RunAsync(
        () => row is null ? Task.FromResult(OperationResult.Cancelled) : repository.History.CherryPickAsync(row.Sha));

    [RelayCommand]
    private Task RevertAsync(CommitRowViewModel? row) => RunAsync(() =>
    {
        if (row is null)
            return Task.FromResult(OperationResult.Cancelled);

        // Reverting a merge needs to know which side to keep; the first parent is the mainline.
        var mainline = row.Commit.IsMerge ? 1 : (int?)null;
        return repository.History.RevertAsync(row.Sha, mainline);
    });

    [RelayCommand]
    private Task ResetSoftAsync(CommitRowViewModel? row) => ResetAsync(row, ResetMode.Soft);

    [RelayCommand]
    private Task ResetMixedAsync(CommitRowViewModel? row) => ResetAsync(row, ResetMode.Mixed);

    [RelayCommand]
    private Task ResetHardAsync(CommitRowViewModel? row) => ResetAsync(row, ResetMode.Hard);

    private Task ResetAsync(CommitRowViewModel? row, ResetMode mode) => RunAsync(async () =>
    {
        if (row is null)
            return OperationResult.Cancelled;

        // Only a hard reset destroys work, so only that one needs a confirmation.
        if (mode == ResetMode.Hard)
        {
            var confirm = ConfirmAsync;
            if (confirm is null ||
                !await confirm($"Reset to {row.ShortSha} and discard all uncommitted changes? This cannot be undone.")
                    .ConfigureAwait(true))
                return OperationResult.Cancelled;
        }

        return await repository.History.ResetAsync(row.Sha, mode).ConfigureAwait(true);
    });

    // ------------------------------------------------------------ gitflow

    [RelayCommand]
    private Task StartFeatureAsync() => StartFlowAsync(FlowBranchKind.Feature);

    [RelayCommand]
    private Task StartReleaseAsync() => StartFlowAsync(FlowBranchKind.Release);

    [RelayCommand]
    private Task StartHotfixAsync() => StartFlowAsync(FlowBranchKind.Hotfix);

    private Task StartFlowAsync(FlowBranchKind kind) => RunAsync(async () =>
    {
        var name = await PromptForAsync(
            $"Start a {kind.ToString().ToLowerInvariant()}",
            $"Name for the new {kind.ToString().ToLowerInvariant()} branch:").ConfigureAwait(true);

        return name is null
            ? OperationResult.Cancelled
            : await repository.Flow.StartAsync(kind, name).ConfigureAwait(true);
    });

    /// <summary>
    /// Finishes whichever flow branch is currently checked out. Merging into two branches and then
    /// deleting is enough of a rewrite to be worth confirming.
    /// </summary>
    [RelayCommand]
    private Task FinishCurrentFlowBranchAsync() => RunAsync(async () =>
    {
        var branch = await repository.GetCurrentBranchAsync().ConfigureAwait(true);
        if (branch is null)
            return OperationResult.Fail("HEAD is detached, so there is no branch to finish.");

        var config = await repository.Flow.GetConfigAsync().ConfigureAwait(true);
        FlowBranchKind? kind = branch switch
        {
            _ when branch.StartsWith(config.FeaturePrefix, StringComparison.Ordinal) => FlowBranchKind.Feature,
            _ when branch.StartsWith(config.ReleasePrefix, StringComparison.Ordinal) => FlowBranchKind.Release,
            _ when branch.StartsWith(config.HotfixPrefix, StringComparison.Ordinal) => FlowBranchKind.Hotfix,
            _ => null,
        };

        if (kind is not { } flowKind)
            return OperationResult.Fail($"'{branch}' is not a git-flow branch.");

        var targets = flowKind == FlowBranchKind.Feature
            ? config.Develop
            : $"{config.Main} and {config.Develop}";

        var confirm = ConfirmAsync;
        if (confirm is null ||
            !await confirm($"Merge '{branch}' into {targets}, then delete it?").ConfigureAwait(true))
            return OperationResult.Cancelled;

        return await repository.Flow.FinishAsync(flowKind, branch).ConfigureAwait(true);
    });

    // -------------------------------------------------------------- stash

    [RelayCommand]
    private Task StashPushAsync() => RunAsync(async () =>
    {
        var message = await PromptForAsync("Stash changes", "Optional description:").ConfigureAwait(true);
        return message is null
            ? OperationResult.Cancelled
            : await repository.Stashes.PushAsync(message, includeUntracked: true).ConfigureAwait(true);
    });

    [RelayCommand]
    private Task StashApplyAsync(SidebarItemViewModel? item) => RunAsync(
        () => item is null ? Task.FromResult(OperationResult.Cancelled) : repository.Stashes.ApplyAsync(StashReference(item)));

    [RelayCommand]
    private Task StashPopAsync(SidebarItemViewModel? item) => RunAsync(
        () => item is null ? Task.FromResult(OperationResult.Cancelled) : repository.Stashes.PopAsync(StashReference(item)));

    [RelayCommand]
    private Task StashDropAsync(SidebarItemViewModel? item) => RunAsync(async () =>
    {
        if (item is null)
            return OperationResult.Cancelled;

        var confirm = ConfirmAsync;
        if (confirm is null ||
            !await confirm($"Drop {StashReference(item)}? The stashed changes cannot be recovered.")
                .ConfigureAwait(true))
            return OperationResult.Cancelled;

        return await repository.Stashes.DropAsync(StashReference(item)).ConfigureAwait(true);
    });

    /// <summary>Sidebar stash entries carry their real reference, e.g. "stash@{2}".</summary>
    private static string StashReference(SidebarItemViewModel item) => item.Ref.FullName;

    // ------------------------------------------------------------ plumbing

    private async Task<string?> PromptForAsync(string title, string message, string initial = "")
    {
        var prompt = PromptAsync;
        if (prompt is null)
            return null;

        var value = await prompt(new PromptRequest(title, message, initial)).ConfigureAwait(true);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Runs an operation, reports its outcome, and asks the shell to reload.</summary>
    private Task RunAsync(Func<Task<OperationResult>> operation)
    {
        PendingOperation = ExecuteAsync(operation);
        return PendingOperation;
    }

    private async Task ExecuteAsync(Func<Task<OperationResult>> operation)
    {
        try
        {
            Report(await operation().ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            IsError = false;
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
            IsError = true;
        }
        finally
        {
            await RefreshStateAsync().ConfigureAwait(true);
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Report(OperationResult result)
    {
        // Backing out of a prompt is not an outcome worth announcing.
        if (result.Outcome == OperationOutcome.Cancelled)
            return;

        StatusText = result.Message;
        IsError = result.Outcome == OperationOutcome.Failed;
    }

    public void Dispose()
    {
        _networkCts?.Dispose();
        _networkCts = null;
    }

    private Task RunNetworkAsync(Func<IProgress<string>, CancellationToken, Task<OperationResult>> operation) =>
        RunAsync(async () => await RunNetworkOperationAsync(operation).ConfigureAwait(true));

    /// <summary>Runs a network operation with progress reporting and cancellation.</summary>
    private async Task<OperationResult> RunNetworkOperationAsync(
        Func<IProgress<string>, CancellationToken, Task<OperationResult>> operation)
    {
        _networkCts?.Dispose();
        _networkCts = new CancellationTokenSource();

        IsNetworkOperationRunning = true;
        ProgressText = null;

        try
        {
            var progress = new Progress<string>(line => ProgressText = line);
            return await operation(progress, _networkCts.Token).ConfigureAwait(true);
        }
        finally
        {
            IsNetworkOperationRunning = false;
            ProgressText = null;
        }
    }
}
