namespace Grove.Core;

/// <summary>Which side of the index a working-tree diff is taken from.</summary>
public enum DiffSide
{
    /// <summary>Index versus working tree: the changes not yet staged.</summary>
    Unstaged,

    /// <summary>HEAD versus index: the changes that a commit would include.</summary>
    Staged,
}

/// <summary>
/// The write half of the git API: staging, discarding and committing. Kept separate from
/// <see cref="GitRepository"/> so the read path stays obviously side-effect free.
/// </summary>
public sealed class GitWorkingCopy(GitCommandRunner git)
{
    // -------------------------------------------------------------- diffs

    /// <summary>
    /// Diff for one working-tree file, structured into hunks so individual hunks or lines can be
    /// staged. Untracked files have no diff, so one is synthesised against an empty blob.
    /// </summary>
    public async Task<FileDiff?> GetFileDiffAsync(
        FileChange file, DiffSide side, bool isUntracked = false, DiffOptions? options = null,
        CancellationToken ct = default)
    {
        var diffOptions = options ?? DiffOptions.Default;

        var args = new List<string> { "diff", "--no-color" };
        args.AddRange(diffOptions.ToArguments());
        if (side == DiffSide.Staged)
            args.Add("--cached");

        if (isUntracked)
        {
            // An untracked file is not known to git at all; --no-index diffs it against nothing.
            args = ["diff", "--no-color", .. diffOptions.ToArguments(), "--no-index", "--", "/dev/null", file.Path];
        }
        else
        {
            args.Add("--");
            args.Add(file.Path);
            if (file.OldPath is not null)
                args.Add(file.OldPath);
        }

        var result = await git.RunAsync(args, ct).ConfigureAwait(false);

        // --no-index reports "differences found" as exit code 1, which is not a failure here.
        if (!result.Success && result.StdOut.Length == 0)
            return null;

        // As above: no hunks means no differences under these options, not a failure.
        var files = DiffParser.ParseFiles(result.StdOut);
        return files.Count > 0
            ? files[0]
            : new FileDiff { HeaderLines = [], Hunks = [], Path = file.Path };
    }

    // ------------------------------------------------------------ staging

    /// <summary>Stages a whole file, including deletions and untracked files.</summary>
    public Task StageAsync(IEnumerable<string> paths, CancellationToken ct = default) =>
        RunEnsuringSuccess(["add", "--", .. paths], "git add", ct);

    /// <summary>Removes a file's staged changes, leaving the working tree untouched.</summary>
    public async Task UnstageAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return;

        var result = await git.RunAsync(["restore", "--staged", "--", .. pathList], ct).ConfigureAwait(false);

        // Before the first commit there is no HEAD to restore from, so fall back to emptying
        // the index entry directly.
        if (!result.Success)
            (await git.RunAsync(["rm", "--cached", "-f", "--", .. pathList], ct).ConfigureAwait(false))
                .EnsureSuccess("git rm --cached");
    }

    /// <summary>Applies a synthesised patch to the index, for hunk- and line-level staging.</summary>
    public async Task ApplyToIndexAsync(string patch, PatchDirection direction, CancellationToken ct = default)
    {
        var args = new List<string> { "apply", "--cached", "--unidiff-zero", "--whitespace=nowarn" };
        if (direction == PatchDirection.Unstage)
            args.Add("--reverse");
        args.Add("-");

        var result = await git.RunAsync(args, patch, ct).ConfigureAwait(false);
        result.EnsureSuccess("git apply --cached");
    }

    // ---------------------------------------------------------- discarding

    /// <summary>
    /// Throws away unstaged changes to tracked files. Destructive and unrecoverable, so callers
    /// must confirm with the user first.
    /// </summary>
    public Task DiscardChangesAsync(IEnumerable<string> paths, CancellationToken ct = default) =>
        RunEnsuringSuccess(["restore", "--worktree", "--", .. paths], "git restore", ct);

    /// <summary>Deletes untracked files. Destructive; callers must confirm with the user first.</summary>
    public Task DeleteUntrackedAsync(IEnumerable<string> paths, CancellationToken ct = default) =>
        RunEnsuringSuccess(["clean", "-f", "--", .. paths], "git clean", ct);

    // --------------------------------------------------------- committing

    /// <summary>Commits whatever is staged. Returns the new commit's sha.</summary>
    public async Task<string> CommitAsync(string message, bool amend = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new GitException("A commit message is required.");

        var args = new List<string> { "commit", "--quiet", "--file=-", "--cleanup=strip" };
        if (amend)
            args.Add("--amend");

        var result = await git.RunAsync(args, message, ct).ConfigureAwait(false);
        result.EnsureSuccess("git commit");

        var head = await git.RunAsync(ct, "rev-parse", "HEAD").ConfigureAwait(false);
        return head.EnsureSuccess("git rev-parse").Trim();
    }

    /// <summary>Full message of HEAD, used to prefill the box when amending.</summary>
    public async Task<string> GetHeadMessageAsync(CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "log", "-1", "--format=%B").ConfigureAwait(false);
        return result.Success ? result.StdOut.TrimEnd('\n') : string.Empty;
    }

    /// <summary>Recent commit subjects, for the "reuse a recent message" affordance.</summary>
    public async Task<IReadOnlyList<string>> GetRecentMessagesAsync(int count = 10, CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "log", $"--max-count={count}", "--format=%s").ConfigureAwait(false);
        if (!result.Success)
            return [];

        return [.. result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)];
    }

    private async Task RunEnsuringSuccess(List<string> args, string context, CancellationToken ct)
    {
        // "--" with no paths would apply to the whole tree, so an empty selection must do nothing.
        if (args[^1] == "--")
            return;

        var result = await git.RunAsync(args, ct).ConfigureAwait(false);
        result.EnsureSuccess(context);
    }
}
