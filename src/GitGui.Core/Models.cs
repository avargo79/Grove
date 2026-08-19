namespace GitGui.Core;

/// <summary>One entry from <c>git log</c>. Body is loaded lazily via <see cref="GitRepository.GetCommitDetailAsync"/>.</summary>
public sealed record Commit(
    string Sha,
    IReadOnlyList<string> ParentShas,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string CommitterName,
    DateTimeOffset CommitDate,
    string Subject,
    IReadOnlyList<string> RefNames)
{
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
    public bool IsMerge => ParentShas.Count > 1;
}

public enum RefKind { LocalBranch, RemoteBranch, Tag, Stash, Other }

public sealed record GitRef(
    string FullName,
    string ShortName,
    RefKind Kind,
    string TargetSha,
    string? Upstream,
    int Ahead,
    int Behind,
    bool IsHead)
{
    /// <summary>Remote name for a remote-tracking ref (e.g. "origin"), otherwise null.</summary>
    public string? RemoteName => Kind == RefKind.RemoteBranch && ShortName.Contains('/')
        ? ShortName[..ShortName.IndexOf('/')]
        : null;

    /// <summary>Branch name without the leading remote, for grouping under a remote node.</summary>
    public string NameWithinRemote => RemoteName is { } r ? ShortName[(r.Length + 1)..] : ShortName;
}

public enum ChangeKind { Added, Modified, Deleted, Renamed, Copied, TypeChanged, Unmerged, Unknown }

public sealed record FileChange(ChangeKind Kind, string Path, string? OldPath = null)
{
    public string DisplayPath => OldPath is null ? Path : $"{OldPath} → {Path}";
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? string.Empty;
}

public sealed record CommitDetail(Commit Commit, string Body, IReadOnlyList<FileChange> Files);

public enum DiffLineKind { Header, HunkHeader, Added, Removed, Context, NoNewline }

/// <summary>A single rendered diff line. Line numbers are null where the side has no line.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber, int? NewLineNumber);

/// <summary>Working-tree state: which files are staged vs unstaged, plus branch position.</summary>
public sealed record WorkingTreeStatus(
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<FileChange> Staged,
    IReadOnlyList<FileChange> Unstaged,
    IReadOnlyList<FileChange> Untracked)
{
    public int TotalChanges => Staged.Count + Unstaged.Count + Untracked.Count;
    public bool IsClean => TotalChanges == 0;

    public static WorkingTreeStatus Empty { get; } =
        new(null, null, 0, 0, [], [], []);
}
