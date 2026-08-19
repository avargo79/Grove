namespace GitFork.Core;

/// <summary>Branch and tag lifecycle: checkout, create, rename, delete.</summary>
public sealed class GitRefOperations(GitCommandRunner git)
{
    // ------------------------------------------------------------ checkout

    /// <summary>Switches to an existing branch.</summary>
    public async Task<OperationResult> CheckoutBranchAsync(string branch, CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "switch", branch).ConfigureAwait(false);
        return Interpret(result, $"Checked out '{branch}'.");
    }

    /// <summary>
    /// Checks out a commit directly, leaving HEAD detached — which is what Fork does when you
    /// double-click a commit that has no branch on it.
    /// </summary>
    public async Task<OperationResult> CheckoutCommitAsync(string sha, CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "checkout", "--detach", sha).ConfigureAwait(false);
        return Interpret(result, $"Checked out {Short(sha)} (detached HEAD).");
    }

    /// <summary>Checks out a remote branch by creating a local branch that tracks it.</summary>
    public async Task<OperationResult> CheckoutRemoteBranchAsync(
        string remoteBranch, CancellationToken ct = default)
    {
        // "origin/feature" becomes local "feature" tracking the remote, as git's own DWIM does.
        var localName = remoteBranch.Contains('/')
            ? remoteBranch[(remoteBranch.IndexOf('/') + 1)..]
            : remoteBranch;

        var existing = await git.RunAsync(ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{localName}")
            .ConfigureAwait(false);

        // If the local branch already exists, switch to it rather than failing to recreate it.
        var result = existing.Success
            ? await git.RunAsync(ct, "switch", localName).ConfigureAwait(false)
            : await git.RunAsync(ct, "switch", "--track", "-c", localName, remoteBranch).ConfigureAwait(false);

        return Interpret(result, $"Checked out '{localName}'.");
    }

    // -------------------------------------------------------------- create

    /// <summary>Creates a branch and optionally switches to it.</summary>
    public async Task<OperationResult> CreateBranchAsync(
        string name, string? startPoint = null, bool checkout = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return OperationResult.Fail("A branch name is required.");

        var args = checkout
            ? new List<string> { "switch", "-c", name }
            : ["branch", name];

        if (!string.IsNullOrEmpty(startPoint))
            args.Add(startPoint);

        var result = await git.RunAsync(args, ct).ConfigureAwait(false);
        return Interpret(result, $"Created branch '{name}'.");
    }

    public async Task<OperationResult> RenameBranchAsync(
        string oldName, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return OperationResult.Fail("A branch name is required.");

        var result = await git.RunAsync(ct, "branch", "-m", oldName, newName).ConfigureAwait(false);
        return Interpret(result, $"Renamed '{oldName}' to '{newName}'.");
    }

    /// <summary>
    /// Deletes a local branch. Without <paramref name="force"/> git refuses to delete a branch
    /// whose commits are not merged anywhere, which is the safety net worth keeping.
    /// </summary>
    public async Task<OperationResult> DeleteBranchAsync(
        string name, bool force = false, CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "branch", force ? "-D" : "-d", name).ConfigureAwait(false);

        if (!result.Success && result.StdErr.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail(
                $"'{name}' has commits that are not merged anywhere. Deleting it will lose them.");

        return Interpret(result, $"Deleted branch '{name}'.");
    }

    /// <summary>True when the branch has commits that exist nowhere else.</summary>
    public async Task<bool> IsBranchMergedAsync(string name, CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "branch", "--merged", "HEAD", "--format=%(refname:short)")
            .ConfigureAwait(false);

        return result.Success && result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Trim() == name);
    }

    // ---------------------------------------------------------------- tags

    /// <summary>Creates a lightweight tag, or an annotated one when a message is supplied.</summary>
    public async Task<OperationResult> CreateTagAsync(
        string name, string? target = null, string? message = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return OperationResult.Fail("A tag name is required.");

        var args = new List<string> { "tag" };
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("--annotate");
            args.Add("--message");
            args.Add(message);
        }

        args.Add(name);
        if (!string.IsNullOrEmpty(target))
            args.Add(target);

        var result = await git.RunAsync(args, ct).ConfigureAwait(false);
        return Interpret(result, $"Created tag '{name}'.");
    }

    public async Task<OperationResult> DeleteTagAsync(string name, CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "tag", "-d", name).ConfigureAwait(false);
        return Interpret(result, $"Deleted tag '{name}'.");
    }

    // ------------------------------------------------------------- helpers

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    /// <summary>Turns a git exit code into a result, preferring git's own wording on failure.</summary>
    internal static OperationResult Interpret(GitResult result, string successMessage)
    {
        if (result.Success)
            return OperationResult.Ok(successMessage);

        var message = result.StdErr.Trim();
        if (message.Length == 0)
            message = result.StdOut.Trim();

        return OperationResult.Fail(message.Length == 0 ? "The operation failed." : message);
    }
}
