namespace GitFork.Core;

/// <summary>Whether a commit carries a usable signature.</summary>
public enum SignatureStatus
{
    /// <summary>Not signed at all.</summary>
    None,

    /// <summary>Signed, and the signature verifies against a trusted key.</summary>
    Good,

    /// <summary>Signed and valid, but the key is not marked as trusted.</summary>
    Untrusted,

    /// <summary>Signed, but the signature does not verify.</summary>
    Bad,

    /// <summary>Signed by a key this machine does not have.</summary>
    Unknown,
}

/// <summary>A submodule and how far it has drifted from what the parent records.</summary>
public sealed record Submodule(string Path, string Sha, string? Describe, SubmoduleState State)
{
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

/// <summary>What git reports about a submodule's working state.</summary>
public enum SubmoduleState
{
    /// <summary>Checked out at the recorded commit.</summary>
    UpToDate,

    /// <summary>Not initialised, so its directory is empty.</summary>
    NotInitialised,

    /// <summary>Checked out at a different commit than the parent records.</summary>
    OutOfDate,

    /// <summary>Has merge conflicts.</summary>
    Conflicted,
}

/// <summary>A file tracked by git-lfs.</summary>
public sealed record LfsFile(string ObjectId, string Path, bool IsDownloaded);

/// <summary>A git-lfs lock held on a file.</summary>
public sealed record LfsLock(string Id, string Path, string Owner);

/// <summary>
/// The integrations that are only present in some repositories: signatures, submodules and LFS.
/// Each one degrades quietly when the tooling or configuration is absent.
/// </summary>
public sealed class GitIntegrationOperations(GitCommandRunner git)
{
    // --------------------------------------------------------- signatures

    /// <summary>
    /// Signature status per commit, keyed by sha. Read through the log format rather than
    /// <c>--show-signature</c>, which shells out to gpg once per commit and is far too slow to
    /// run over a whole history.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SignatureStatus>> GetSignatureStatusAsync(
        int maxCount = 2000, CancellationToken ct = default)
    {
        var result = await git
            .RunAsync(ct, "log", "--all", $"--max-count={maxCount}", "--format=%H %G?")
            .ConfigureAwait(false);

        if (!result.Success)
            return new Dictionary<string, SignatureStatus>(StringComparer.Ordinal);

        var statuses = new Dictionary<string, SignatureStatus>(StringComparer.Ordinal);
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var space = line.IndexOf(' ');
            if (space < 0)
                continue;

            var status = ParseSignatureCode(line[(space + 1)..].Trim());
            if (status != SignatureStatus.None)
                statuses[line[..space]] = status;
        }

        return statuses;
    }

    /// <summary>Maps git's <c>%G?</c> codes.</summary>
    internal static SignatureStatus ParseSignatureCode(string code) => code switch
    {
        "G" => SignatureStatus.Good,
        "U" => SignatureStatus.Untrusted,
        "B" or "X" or "Y" or "R" => SignatureStatus.Bad,
        "E" => SignatureStatus.Unknown,
        _ => SignatureStatus.None,
    };

    // --------------------------------------------------------- submodules

    public async Task<IReadOnlyList<Submodule>> GetSubmodulesAsync(CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "submodule", "status", "--recursive").ConfigureAwait(false);
        if (!result.Success)
            return [];

        var submodules = new List<Submodule>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 2)
                continue;

            // The leading marker says the state: ' ' fine, '-' uninitialised, '+' moved, 'U' conflicted.
            var state = line[0] switch
            {
                '-' => SubmoduleState.NotInitialised,
                '+' => SubmoduleState.OutOfDate,
                'U' => SubmoduleState.Conflicted,
                _ => SubmoduleState.UpToDate,
            };

            var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            submodules.Add(new Submodule(
                Path: parts[1],
                Sha: parts[0],
                Describe: parts.Length > 2 ? parts[2].Trim('(', ')') : null,
                State: state));
        }

        return submodules;
    }

    /// <summary>Initialises and updates submodules, reporting progress as it clones.</summary>
    public async Task<OperationResult> UpdateSubmodulesAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = await git
            .RunWithProgressAsync(["submodule", "update", "--init", "--recursive", "--progress"], progress, ct)
            .ConfigureAwait(false);

        return GitRefOperations.Interpret(result, "Submodules updated.");
    }

    // ---------------------------------------------------------------- LFS

    /// <summary>True when git-lfs is installed and this repository uses it.</summary>
    public async Task<bool> IsLfsEnabledAsync(CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "lfs", "env").ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>
    /// Files tracked by LFS. An asterisk in the listing means the object is present locally; a
    /// minus means only the pointer is.
    /// </summary>
    public async Task<IReadOnlyList<LfsFile>> GetLfsFilesAsync(CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "lfs", "ls-files").ConfigureAwait(false);
        if (!result.Success)
            return [];

        var files = new List<LfsFile>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<oid> <*|-> <path>"
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            files.Add(new LfsFile(parts[0], parts[2].Trim(), parts[1] == "*"));
        }

        return files;
    }

    /// <summary>Locks currently held, which is what LFS is for on binary assets.</summary>
    public async Task<IReadOnlyList<LfsLock>> GetLfsLocksAsync(CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "lfs", "locks").ConfigureAwait(false);
        if (!result.Success)
            return [];

        var locks = new List<LfsLock>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<path>\t<owner>\tID:<id>"
            var parts = line.Split('\t', StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
                continue;

            var id = parts[2].StartsWith("ID:", StringComparison.Ordinal) ? parts[2][3..] : parts[2];
            locks.Add(new LfsLock(id.Trim(), parts[0], parts[1]));
        }

        return locks;
    }
}
