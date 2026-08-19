using System.Globalization;

namespace GitGui.Core;

/// <summary>
/// Read-side API over a repository. Every call shells out to git; nothing is cached here so the
/// view models decide when to refresh.
/// </summary>
public sealed class GitRepository
{
    /// <summary>ASCII unit separator: a safe field delimiter that cannot appear in identities or subjects.</summary>
    private const char Sep = '\u001F';

    private static readonly string LogFormat = string.Join(Sep,
        "%H", "%P", "%an", "%ae", "%aI", "%cn", "%cI", "%D", "%s");

    private readonly GitCommandRunner _git;

    private GitRepository(string rootPath, GitCommandRunner git)
    {
        RootPath = rootPath;
        _git = git;
        WorkingCopy = new GitWorkingCopy(git);
        Refs = new GitRefOperations(git);
        History = new GitHistoryOperations(git);
        Remotes = new GitRemoteOperations(git);
        Stashes = new GitStashOperations(git);
        Files = new GitFileOperations(git);
        Rebase = new GitRebaseOperations(git);
        Reflog = new GitReflogOperations(git);
        Integrations = new GitIntegrationOperations(git);
        Flow = new GitFlowOperations(git, Refs, History);
    }

    /// <summary>The write half of the API: staging, discarding and committing.</summary>
    public GitWorkingCopy WorkingCopy { get; }

    /// <summary>Branch and tag lifecycle.</summary>
    public GitRefOperations Refs { get; }

    /// <summary>Merge, rebase, cherry-pick, revert and reset.</summary>
    public GitHistoryOperations History { get; }

    /// <summary>Fetch, pull and push.</summary>
    public GitRemoteOperations Remotes { get; }

    /// <summary>Stash lifecycle.</summary>
    public GitStashOperations Stashes { get; }

    /// <summary>Blame, per-file history, revision trees and raw blobs.</summary>
    public GitFileOperations Files { get; }

    /// <summary>Interactive rebase.</summary>
    public GitRebaseOperations Rebase { get; }

    /// <summary>The reflog, for recovering unreachable commits.</summary>
    public GitReflogOperations Reflog { get; }

    /// <summary>Signatures, submodules and LFS.</summary>
    public GitIntegrationOperations Integrations { get; }

    /// <summary>Git-flow branch conventions.</summary>
    public GitFlowOperations Flow { get; }

    public string RootPath { get; }
    public string Name => Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, '/'));

    public static async Task<GitRepository?> OpenAsync(string path, CancellationToken ct = default)
    {
        var root = await GitCommandRunner.DiscoverRepositoryRootAsync(path, ct).ConfigureAwait(false);
        return root is null ? null : new GitRepository(root, new GitCommandRunner(root));
    }

    // ---------------------------------------------------------------- commits

    public async Task<IReadOnlyList<Commit>> GetCommitsAsync(
        int maxCount = 2000, bool allRefs = true, CancellationToken ct = default)
    {
        var page = await GetCommitPageAsync(maxCount, allRefs, 0, null, ct).ConfigureAwait(false);
        return page.Commits;
    }

    /// <summary>
    /// One page of history, optionally filtered. <paramref name="skip"/> makes paging possible, so
    /// a long history loads on demand rather than being silently truncated.
    /// </summary>
    public async Task<CommitPage> GetCommitPageAsync(
        int maxCount = 2000, bool allRefs = true, int skip = 0, CommitFilter? filter = null,
        CancellationToken ct = default)
    {
        var effectiveFilter = filter ?? CommitFilter.Empty;

        var args = new List<string> { "log", "--topo-order", $"--pretty=format:{LogFormat}" };

        // One extra, purely to find out whether there is another page behind this one.
        args.Add($"--max-count={maxCount + 1}");
        if (skip > 0)
            args.Add($"--skip={skip}");

        if (allRefs)
        {
            // Deliberately not "--all": that pulls in refs/stash, which would show the stash's
            // internal commits inline in history. Stashes belong in the sidebar.
            args.Add("--branches");
            args.Add("--tags");
            args.Add("--remotes");
            args.Add("HEAD");
        }

        args.AddRange(effectiveFilter.ToArguments());

        if (!string.IsNullOrWhiteSpace(effectiveFilter.Path))
        {
            args.Add("--");
            args.Add(effectiveFilter.Path);
        }

        var result = await _git.RunAsync(args, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            // A repository with no commits yet is not an error worth surfacing: git reports the
            // missing HEAD as an unknown or ambiguous revision.
            string[] emptyRepositorySignals =
            [
                "does not have any commits",
                "bad default revision",
                "unknown revision",
                "ambiguous argument",
            ];
            if (emptyRepositorySignals.Any(signal =>
                    result.StdErr.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                return CommitPage.Empty;

            result.EnsureSuccess("git log");
        }

        var commits = new List<Commit>();
        foreach (var line in result.StdOut.Split('\n'))
        {
            if (line.Length == 0)
                continue;
            var f = line.Split(Sep);
            if (f.Length < 9)
                continue;

            commits.Add(new Commit(
                Sha: f[0],
                ParentShas: f[1].Length == 0 ? [] : f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                AuthorName: f[2],
                AuthorEmail: f[3],
                AuthorDate: ParseDate(f[4]),
                CommitterName: f[5],
                CommitDate: ParseDate(f[6]),
                Subject: f[8],
                RefNames: ParseDecoration(f[7])));
        }

        // Drop the probe commit; its only job was to answer "is there more?".
        var hasMore = commits.Count > maxCount;
        if (hasMore)
            commits.RemoveAt(commits.Count - 1);

        return new CommitPage(commits, hasMore, skip);
    }

    public async Task<CommitDetail> GetCommitDetailAsync(Commit commit, CancellationToken ct = default)
    {
        var bodyTask = _git.RunAsync(ct, "log", "-1", "--format=%B", commit.Sha);
        var filesTask = _git.RunAsync(ct,
            "diff-tree", "--root", "--no-commit-id", "--name-status", "-r", "-M", "-z", commit.Sha);

        var body = (await bodyTask.ConfigureAwait(false)).EnsureSuccess("git log -1").TrimEnd();
        var filesOut = (await filesTask.ConfigureAwait(false)).EnsureSuccess("git diff-tree");

        // Strip the subject line; the UI shows it separately.
        var newline = body.IndexOf('\n');
        var messageBody = newline < 0 ? string.Empty : body[(newline + 1)..].Trim('\n');

        return new CommitDetail(commit, messageBody, ParseNameStatusZ(filesOut));
    }

    /// <summary>Unified diff for one file in one commit, already split into renderable lines.</summary>
    public async Task<IReadOnlyList<DiffLine>> GetCommitFileDiffAsync(
        string sha, FileChange file, DiffOptions? options = null, CancellationToken ct = default)
    {
        var diff = await GetCommitFileDiffStructuredAsync(sha, file, options, ct).ConfigureAwait(false);
        return diff?.ToLines() ?? [];
    }

    /// <summary>The same diff kept as hunks, for the side-by-side and word-level views.</summary>
    public async Task<FileDiff?> GetCommitFileDiffStructuredAsync(
        string sha, FileChange file, DiffOptions? options = null, CancellationToken ct = default)
    {
        var args = new List<string> { "show", "--format=", "--patch", "--no-color" };
        args.AddRange((options ?? DiffOptions.Default).ToArguments());
        args.Add(sha);
        args.Add("--");
        args.Add(file.Path);
        if (file.OldPath is not null)
            args.Add(file.OldPath);

        var result = await _git.RunAsync(args, ct).ConfigureAwait(false);
        if (!result.Success)
            return null;

        // An empty diff is a real answer, not a failure: with whitespace ignored a file whose only
        // change is reindentation legitimately has nothing to show.
        var files = DiffParser.ParseFiles(result.StdOut);
        return files.Count > 0
            ? files[0]
            : new FileDiff { HeaderLines = [], Hunks = [], Path = file.Path };
    }

    // ----------------------------------------------------------------- refs

    public async Task<IReadOnlyList<GitRef>> GetRefsAsync(CancellationToken ct = default)
    {
        var format = string.Join(Sep,
            "%(refname)", "%(objectname)", "%(upstream:short)", "%(upstream:track)", "%(HEAD)");
        var result = await _git.RunAsync(ct, "for-each-ref", $"--format={format}");
        if (!result.Success)
            return [];

        var refs = new List<GitRef>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(Sep);
            if (f.Length < 5)
                continue;

            var fullName = f[0];
            var (kind, shortName) = ClassifyRef(fullName);
            // Remote HEAD pointers (origin/HEAD) are noise in a branch list.
            if (kind == RefKind.RemoteBranch && shortName.EndsWith("/HEAD", StringComparison.Ordinal))
                continue;

            var (ahead, behind) = ParseTrack(f[3]);
            refs.Add(new GitRef(
                FullName: fullName,
                ShortName: shortName,
                Kind: kind,
                TargetSha: f[1],
                Upstream: string.IsNullOrEmpty(f[2]) ? null : f[2],
                Ahead: ahead,
                Behind: behind,
                IsHead: f[4].Trim() == "*"));
        }

        return refs;
    }

    /// <summary>Current branch name, or null when HEAD is detached.</summary>
    public async Task<string?> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        var result = await _git.RunAsync(ct, "symbolic-ref", "--quiet", "--short", "HEAD");
        var name = result.StdOut.Trim();
        return result.Success && name.Length > 0 ? name : null;
    }

    public async Task<string?> GetHeadShaAsync(CancellationToken ct = default)
    {
        var result = await _git.RunAsync(ct, "rev-parse", "HEAD");
        var sha = result.StdOut.Trim();
        return result.Success && sha.Length > 0 ? sha : null;
    }

    // --------------------------------------------------------------- status

    public async Task<WorkingTreeStatus> GetStatusAsync(CancellationToken ct = default)
    {
        // "all" rather than "normal": a staging UI needs the individual files inside an
        // untracked directory, not a single collapsed "dir/" entry.
        var result = await _git.RunAsync(ct, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all");
        if (!result.Success)
            return WorkingTreeStatus.Empty;

        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var staged = new List<FileChange>();
        var unstaged = new List<FileChange>();
        var untracked = new List<FileChange>();

        var entries = result.StdOut.Split('\0');
        var cursor = 0;
        while (cursor < entries.Length)
        {
            var entry = entries[cursor++];
            if (entry.Length == 0)
                continue;

            switch (entry[0])
            {
                case '#':
                    var header = entry.Split(' ', 3);
                    if (header.Length >= 3)
                    {
                        if (header[1] == "branch.head" && header[2] != "(detached)")
                            branch = header[2];
                        else if (header[1] == "branch.upstream")
                            upstream = header[2];
                        else if (header[1] == "branch.ab")
                            (ahead, behind) = ParseAheadBehind(header[2]);
                    }
                    break;

                case '1': // ordinary change: "1 XY sub mH mI mW hH hI path"
                {
                    var parts = entry.Split(' ', 9);
                    if (parts.Length < 9)
                        break;
                    AddStatusEntry(parts[1], parts[8], null, staged, unstaged);
                    break;
                }

                case '2': // rename/copy; the original path is the next NUL-separated field
                {
                    var parts = entry.Split(' ', 10);
                    if (parts.Length < 10)
                        break;
                    var oldPath = cursor < entries.Length ? entries[cursor++] : null;
                    AddStatusEntry(parts[1], parts[9], oldPath, staged, unstaged);
                    break;
                }

                case 'u': // unmerged
                {
                    var parts = entry.Split(' ', 11);
                    if (parts.Length >= 11)
                        unstaged.Add(new FileChange(ChangeKind.Unmerged, parts[10]));
                    break;
                }

                case '?':
                    untracked.Add(new FileChange(ChangeKind.Added, entry[2..]));
                    break;
            }
        }

        return new WorkingTreeStatus(branch, upstream, ahead, behind, staged, unstaged, untracked);
    }

    // --------------------------------------------------------------- parsing

    private static void AddStatusEntry(
        string xy, string path, string? oldPath, List<FileChange> staged, List<FileChange> unstaged)
    {
        if (xy.Length < 2)
            return;
        if (xy[0] != '.')
            staged.Add(new FileChange(MapStatusCode(xy[0]), path, oldPath));
        if (xy[1] != '.')
            unstaged.Add(new FileChange(MapStatusCode(xy[1]), path, oldPath));
    }

    private static ChangeKind MapStatusCode(char c) => c switch
    {
        'A' => ChangeKind.Added,
        'M' => ChangeKind.Modified,
        'D' => ChangeKind.Deleted,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'T' => ChangeKind.TypeChanged,
        'U' => ChangeKind.Unmerged,
        _ => ChangeKind.Unknown,
    };

    /// <summary>Parses the NUL-separated output of <c>--name-status -z</c>.</summary>
    internal static IReadOnlyList<FileChange> ParseNameStatusZ(string output)
    {
        var files = new List<FileChange>();
        var fields = output.Split('\0');
        var cursor = 0;

        while (cursor < fields.Length)
        {
            var status = fields[cursor++];
            if (status.Length == 0)
                continue;

            var kind = MapStatusCode(status[0]);
            // Renames and copies carry a similarity score and consume two path fields.
            var pathFields = kind is ChangeKind.Renamed or ChangeKind.Copied ? 2 : 1;

            // A status code with no path behind it means the output was cut short.
            if (cursor + pathFields > fields.Length || fields[cursor + pathFields - 1].Length == 0)
                break;

            files.Add(pathFields == 2
                ? new FileChange(kind, fields[cursor + 1], fields[cursor])
                : new FileChange(kind, fields[cursor]));

            cursor += pathFields;
        }

        return files;
    }

    private static (RefKind Kind, string ShortName) ClassifyRef(string fullName) => fullName switch
    {
        _ when fullName.StartsWith("refs/heads/", StringComparison.Ordinal)
            => (RefKind.LocalBranch, fullName["refs/heads/".Length..]),
        _ when fullName.StartsWith("refs/remotes/", StringComparison.Ordinal)
            => (RefKind.RemoteBranch, fullName["refs/remotes/".Length..]),
        _ when fullName.StartsWith("refs/tags/", StringComparison.Ordinal)
            => (RefKind.Tag, fullName["refs/tags/".Length..]),
        _ when fullName.StartsWith("refs/stash", StringComparison.Ordinal)
            => (RefKind.Stash, "stash"),
        _ => (RefKind.Other, fullName),
    };

    /// <summary>Parses <c>%(upstream:track)</c>, e.g. "[ahead 2, behind 1]".</summary>
    internal static (int Ahead, int Behind) ParseTrack(string track)
    {
        int ahead = 0, behind = 0;
        foreach (var part in track.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(' ');
            if (bits.Length != 2 || !int.TryParse(bits[1], out var n))
                continue;
            if (bits[0] == "ahead")
                ahead = n;
            else if (bits[0] == "behind")
                behind = n;
        }

        return (ahead, behind);
    }

    /// <summary>Parses the porcelain-v2 "branch.ab" header, e.g. "+2 -1".</summary>
    private static (int Ahead, int Behind) ParseAheadBehind(string value)
    {
        int ahead = 0, behind = 0;
        foreach (var p in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p.StartsWith('+') && int.TryParse(p[1..], out var a))
                ahead = a;
            else if (p.StartsWith('-') && int.TryParse(p[1..], out var b))
                behind = b;
        }

        return (ahead, behind);
    }

    /// <summary>Splits the <c>%D</c> decoration into individual ref names.</summary>
    internal static IReadOnlyList<string> ParseDecoration(string decoration)
    {
        if (string.IsNullOrWhiteSpace(decoration))
            return [];

        var names = new List<string>();
        foreach (var raw in decoration.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // "HEAD -> main" means HEAD points at main; keep both so the UI can badge the checkout.
            if (raw.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                names.Add("HEAD");
                names.Add(raw["HEAD -> ".Length..]);
            }
            else
            {
                // The "tag: " prefix is kept so callers can tell tags from branches.
                names.Add(raw);
            }
        }

        return names;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : DateTimeOffset.MinValue;
}
