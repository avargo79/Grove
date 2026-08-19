namespace Grove.Core;

/// <summary>A configured remote.</summary>
public sealed record GitRemote(string Name, string FetchUrl, string PushUrl);

/// <summary>How a pull reconciles local and remote work.</summary>
public enum PullStrategy
{
    /// <summary>Merge the fetched work in.</summary>
    Merge,

    /// <summary>Replay local commits on top of the fetched work.</summary>
    Rebase,

    /// <summary>Refuse unless the update is a fast-forward.</summary>
    FastForwardOnly,
}

/// <summary>
/// Network operations. Credentials are never handled here: git's own credential helper supplies
/// them, and terminal prompting is disabled so a missing credential fails fast instead of hanging.
/// </summary>
public sealed class GitRemoteOperations(GitCommandRunner git)
{
    public async Task<IReadOnlyList<GitRemote>> GetRemotesAsync(CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "remote", "-v").ConfigureAwait(false);
        if (!result.Success)
            return [];

        var fetchUrls = new Dictionary<string, string>(StringComparer.Ordinal);
        var pushUrls = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format is "name\turl (fetch)" / "name\turl (push)".
            var tab = line.IndexOf('\t');
            if (tab < 0)
                continue;

            var name = line[..tab];
            var rest = line[(tab + 1)..];
            var space = rest.LastIndexOf(' ');
            if (space < 0)
                continue;

            var url = rest[..space];
            if (rest.EndsWith("(push)", StringComparison.Ordinal))
                pushUrls[name] = url;
            else
                fetchUrls[name] = url;
        }

        return
        [
            .. fetchUrls.Keys.Union(pushUrls.Keys, StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(name => new GitRemote(
                    name,
                    fetchUrls.GetValueOrDefault(name, string.Empty),
                    pushUrls.GetValueOrDefault(name, fetchUrls.GetValueOrDefault(name, string.Empty)))),
        ];
    }

    /// <summary>Fetches from one remote, or all of them, pruning refs that no longer exist.</summary>
    public async Task<OperationResult> FetchAsync(
        string? remote = null, bool prune = true, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "fetch", "--progress", "--tags" };
        if (prune)
            args.Add("--prune");
        if (string.IsNullOrEmpty(remote))
            args.Add("--all");
        else
            args.Add(remote);

        var result = await git.RunWithProgressAsync(args, progress, ct).ConfigureAwait(false);
        return InterpretNetwork(result, remote is null ? "Fetched all remotes." : $"Fetched '{remote}'.");
    }

    /// <summary>Fetches and integrates, by merge, rebase or fast-forward only.</summary>
    public async Task<OperationResult> PullAsync(
        PullStrategy strategy = PullStrategy.Merge, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "pull", "--progress", "--no-edit" };
        args.Add(strategy switch
        {
            PullStrategy.Rebase => "--rebase",
            PullStrategy.FastForwardOnly => "--ff-only",
            _ => "--no-rebase",
        });

        var result = await git.RunWithProgressAsync(args, progress, ct).ConfigureAwait(false);
        if (result.Success)
            return OperationResult.Ok("Pull complete.");

        // A pull that stops on conflicts has still done its job up to that point.
        var conflicts = await new GitHistoryOperations(git).GetConflictedPathsAsync(ct).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            var noun = conflicts.Count == 1 ? "file" : "files";
            return OperationResult.Conflict($"Pull stopped with conflicts in {conflicts.Count} {noun}.", conflicts);
        }

        return InterpretNetwork(result, "Pull complete.");
    }

    /// <summary>
    /// Pushes the current branch. <paramref name="forceWithLease"/> is offered instead of a plain
    /// force because it refuses when the remote has moved in a way you have not seen.
    /// </summary>
    public async Task<OperationResult> PushAsync(
        string? remote = null, string? branch = null, bool setUpstream = false,
        bool forceWithLease = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var args = new List<string> { "push", "--progress" };
        if (forceWithLease)
            args.Add("--force-with-lease");
        if (setUpstream)
            args.Add("--set-upstream");

        if (!string.IsNullOrEmpty(remote))
        {
            args.Add(remote);
            if (!string.IsNullOrEmpty(branch))
                args.Add(branch);
        }

        var result = await git.RunWithProgressAsync(args, progress, ct).ConfigureAwait(false);

        // Pushing a branch with no upstream fails with advice rather than doing something surprising.
        if (!result.Success &&
            result.StdErr.Contains("has no upstream branch", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail(
                "This branch has no upstream. Push again with \"set upstream\" to create one.");

        return InterpretNetwork(result, "Push complete.");
    }

    /// <summary>Deletes a branch on the remote. Destructive for everyone, not just you.</summary>
    public async Task<OperationResult> DeleteRemoteBranchAsync(
        string remote, string branch, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = await git
            .RunWithProgressAsync(["push", "--progress", remote, "--delete", branch], progress, ct)
            .ConfigureAwait(false);

        return InterpretNetwork(result, $"Deleted '{branch}' on '{remote}'.");
    }

    /// <summary>Turns a network failure into something worth showing a user.</summary>
    private static OperationResult InterpretNetwork(GitResult result, string successMessage)
    {
        if (result.Success)
            return OperationResult.Ok(successMessage);

        var error = result.StdErr.Trim();

        if (error.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail(
                "Authentication failed. Grove never handles credentials itself — configure a git " +
                "credential helper, or an SSH key, and try again.");

        if (error.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail("Could not reach the remote. Check your network connection.");

        if (error.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail("The remote has commits you do not have. Pull before pushing.");

        return OperationResult.Fail(error.Length == 0 ? "The operation failed." : error);
    }
}
