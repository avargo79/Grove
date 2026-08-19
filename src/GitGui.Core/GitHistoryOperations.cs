namespace GitGui.Core;

/// <summary>How far a reset moves the index and working tree along with HEAD.</summary>
public enum ResetMode
{
    /// <summary>Moves HEAD only; changes stay staged.</summary>
    Soft,

    /// <summary>Moves HEAD and resets the index; changes stay in the working tree.</summary>
    Mixed,

    /// <summary>Moves HEAD and throws away index and working tree changes. Unrecoverable.</summary>
    Hard,
}

/// <summary>
/// Operations that rewrite or extend history: merge, rebase, cherry-pick, revert and reset, plus
/// the abort/continue pair that any of them can leave the repository needing.
/// </summary>
public sealed class GitHistoryOperations(GitCommandRunner git)
{
    // -------------------------------------------------------------- state

    /// <summary>
    /// Detects an operation the repository is part-way through by the marker files git leaves in
    /// <c>.git</c>, and lists whatever still conflicts.
    /// </summary>
    public async Task<RepositoryState> GetStateAsync(CancellationToken ct = default)
    {
        var gitDirResult = await git.RunAsync(ct, "rev-parse", "--absolute-git-dir").ConfigureAwait(false);
        if (!gitDirResult.Success)
            return RepositoryState.Clean;

        var gitDir = gitDirResult.StdOut.Trim();
        var operation = RepositoryOperation.None;

        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) ||
            Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            operation = RepositoryOperation.Rebase;
        else if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
            operation = RepositoryOperation.Merge;
        else if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
            operation = RepositoryOperation.CherryPick;
        else if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
            operation = RepositoryOperation.Revert;
        else if (File.Exists(Path.Combine(gitDir, "BISECT_LOG")))
            operation = RepositoryOperation.Bisect;

        var conflicts = await GetConflictedPathsAsync(ct).ConfigureAwait(false);
        return new RepositoryState(operation, conflicts);
    }

    /// <summary>Paths with unresolved conflict markers, straight from the index.</summary>
    public async Task<IReadOnlyList<string>> GetConflictedPathsAsync(CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "diff", "--name-only", "--diff-filter=U", "-z").ConfigureAwait(false);
        if (!result.Success)
            return [];

        return [.. result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    // -------------------------------------------------------------- merge

    /// <summary>Merges a ref into the current branch.</summary>
    public async Task<OperationResult> MergeAsync(
        string reference, bool noFastForward = false, CancellationToken ct = default)
    {
        var args = new List<string> { "merge", "--no-edit" };
        if (noFastForward)
            args.Add("--no-ff");
        args.Add(reference);

        var result = await git.RunAsync(args, ct).ConfigureAwait(false);
        return await InterpretPossiblyConflicting(result, $"Merged '{reference}'.", ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- rebase

    /// <summary>Replays the current branch on top of another ref.</summary>
    public async Task<OperationResult> RebaseAsync(string onto, CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "rebase", onto).ConfigureAwait(false);
        return await InterpretPossiblyConflicting(result, $"Rebased onto '{onto}'.", ct).ConfigureAwait(false);
    }

    // ----------------------------------------------- cherry-pick / revert

    public async Task<OperationResult> CherryPickAsync(string sha, CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "cherry-pick", sha).ConfigureAwait(false);
        return await InterpretPossiblyConflicting(result, $"Cherry-picked {Short(sha)}.", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverts a commit by adding an inverse commit. Merge commits need to know which parent to
    /// treat as the mainline, so that is passed through.
    /// </summary>
    public async Task<OperationResult> RevertAsync(
        string sha, int? mainlineParent = null, CancellationToken ct = default)
    {
        var args = new List<string> { "revert", "--no-edit" };
        if (mainlineParent is { } parent)
        {
            args.Add("-m");
            args.Add(parent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        args.Add(sha);

        var result = await git.RunAsync(args, ct).ConfigureAwait(false);
        return await InterpretPossiblyConflicting(result, $"Reverted {Short(sha)}.", ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------- reset

    /// <summary>
    /// Moves the current branch to another commit. <see cref="ResetMode.Hard"/> destroys
    /// uncommitted work, so callers must confirm with the user before using it.
    /// </summary>
    public async Task<OperationResult> ResetAsync(string sha, ResetMode mode, CancellationToken ct = default)
    {
        var flag = mode switch
        {
            ResetMode.Soft => "--soft",
            ResetMode.Hard => "--hard",
            _ => "--mixed",
        };

        var result = await git.RunAsync(ct, "reset", flag, sha).ConfigureAwait(false);
        return GitRefOperations.Interpret(result, $"Reset to {Short(sha)} ({mode.ToString().ToLowerInvariant()}).");
    }

    // ------------------------------------------------- continue and abort

    /// <summary>Resumes whatever multi-step operation is in progress, once conflicts are resolved.</summary>
    public async Task<OperationResult> ContinueAsync(CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct).ConfigureAwait(false);
        if (!state.IsInProgress)
            return OperationResult.Fail("Nothing is in progress.");

        if (state.HasConflicts)
            return OperationResult.Conflict(
                "Resolve the remaining conflicts and stage them first.", state.ConflictedPaths);

        var command = state.Operation switch
        {
            RepositoryOperation.Rebase => "rebase",
            RepositoryOperation.CherryPick => "cherry-pick",
            RepositoryOperation.Revert => "revert",
            _ => "merge",
        };

        var result = await git.RunAsync(ct, command, "--continue").ConfigureAwait(false);
        return await InterpretPossiblyConflicting(result, $"{state.Description} continued.", ct).ConfigureAwait(false);
    }

    /// <summary>Abandons the in-progress operation and restores the pre-operation state.</summary>
    public async Task<OperationResult> AbortAsync(CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct).ConfigureAwait(false);
        if (!state.IsInProgress)
            return OperationResult.Fail("Nothing is in progress.");

        var command = state.Operation switch
        {
            RepositoryOperation.Rebase => "rebase",
            RepositoryOperation.CherryPick => "cherry-pick",
            RepositoryOperation.Revert => "revert",
            RepositoryOperation.Bisect => "bisect",
            _ => "merge",
        };

        var result = state.Operation == RepositoryOperation.Bisect
            ? await git.RunAsync(ct, "bisect", "reset").ConfigureAwait(false)
            : await git.RunAsync(ct, command, "--abort").ConfigureAwait(false);

        return GitRefOperations.Interpret(result, $"Aborted the {command}.");
    }

    // ------------------------------------------------------------ helpers

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    /// <summary>
    /// A non-zero exit from a merge-like command usually means conflicts rather than an error, so
    /// the index is consulted before deciding which it was.
    /// </summary>
    private async Task<OperationResult> InterpretPossiblyConflicting(
        GitResult result, string successMessage, CancellationToken ct)
    {
        if (result.Success)
            return OperationResult.Ok(successMessage);

        var conflicts = await GetConflictedPathsAsync(ct).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            var noun = conflicts.Count == 1 ? "file" : "files";
            return OperationResult.Conflict($"Stopped with conflicts in {conflicts.Count} {noun}.", conflicts);
        }

        return GitRefOperations.Interpret(result, successMessage);
    }
}
