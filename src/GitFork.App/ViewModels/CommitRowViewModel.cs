using System.Globalization;
using GitFork.Core;
using GitFork.Core.Graph;

namespace GitFork.App.ViewModels;

/// <summary>Kind of decoration badge shown next to a commit subject.</summary>
public enum RefBadgeKind { Head, LocalBranch, RemoteBranch, Tag, Stash }

public sealed record RefBadgeViewModel(string Name, RefBadgeKind Kind)
{
    // Style classes bind to these to colour each badge by what it points at.
    public bool IsHead => Kind == RefBadgeKind.Head;
    public bool IsLocal => Kind == RefBadgeKind.LocalBranch;
    public bool IsRemote => Kind == RefBadgeKind.RemoteBranch;
    public bool IsTag => Kind == RefBadgeKind.Tag;
}

/// <summary>One row of the commit list: the commit itself plus its precomputed graph layout.</summary>
public sealed class CommitRowViewModel(
    Commit commit,
    GraphRow graphRow,
    double graphWidth,
    IReadOnlyDictionary<string, RefKind>? refKinds = null,
    SignatureStatus signature = SignatureStatus.None)
{
    public SignatureStatus Signature { get; } = signature;

    public bool IsSigned => Signature != SignatureStatus.None;
    public bool HasGoodSignature => Signature == SignatureStatus.Good;
    public bool HasBadSignature => Signature is SignatureStatus.Bad or SignatureStatus.Unknown;

    /// <summary>A seal for a verified signature, a warning triangle for anything doubtful.</summary>
    public string SignatureGlyph => Signature switch
    {
        SignatureStatus.Good => "\u2713",
        SignatureStatus.Untrusted => "\u2713",
        _ => "\u26A0",
    };

    public string SignatureTooltip => Signature switch
    {
        SignatureStatus.Good => "Signed, and the signature verifies",
        SignatureStatus.Untrusted => "Signed and valid, but the key is not trusted",
        SignatureStatus.Bad => "Signed, but the signature does not verify",
        SignatureStatus.Unknown => "Signed by a key this machine does not have",
        _ => string.Empty,
    };

    public Commit Commit { get; } = commit;
    public GraphRow GraphRow { get; } = graphRow;

    /// <summary>Shared across all rows so the lane columns line up under each other.</summary>
    public double GraphWidth { get; } = graphWidth;

    public string Sha => Commit.Sha;
    public string ShortSha => Commit.ShortSha;
    public string Subject => Commit.Subject;
    public string AuthorName => Commit.AuthorName;
    public string AuthorInitials => GetInitials(Commit.AuthorName);
    public DateTimeOffset Date => Commit.AuthorDate;
    public string DateDisplay => RelativeTime.Format(Commit.AuthorDate);
    public string DateTooltip => Commit.AuthorDate.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);

    public IReadOnlyList<RefBadgeViewModel> Badges { get; } = BuildBadges(commit.RefNames, refKinds);

    public bool HasBadges => Badges.Count > 0;

    private static IReadOnlyList<RefBadgeViewModel> BuildBadges(
        IReadOnlyList<string> refNames, IReadOnlyDictionary<string, RefKind>? refKinds)
    {
        var badges = new List<RefBadgeViewModel>();
        foreach (var name in refNames)
        {
            var (kind, display) = name switch
            {
                "HEAD" => (RefBadgeKind.Head, name),
                _ when name.StartsWith("tag: ", StringComparison.Ordinal)
                    => (RefBadgeKind.Tag, name["tag: ".Length..]),
                _ when name.StartsWith("refs/stash", StringComparison.Ordinal)
                    => (RefBadgeKind.Stash, "stash"),
                _ => (ClassifyBranch(name, refKinds), name),
            };
            badges.Add(new RefBadgeViewModel(display, kind));
        }

        // HEAD first, then locals, then remotes and tags: most specific context nearest the subject.
        return [.. badges.OrderBy(b => b.Kind)];
    }

    /// <summary>
    /// A slash does not imply a remote — "feature/login" is an ordinary local branch. The ref list
    /// is authoritative; the slash heuristic is only a fallback when the ref has since disappeared.
    /// </summary>
    private static RefBadgeKind ClassifyBranch(string name, IReadOnlyDictionary<string, RefKind>? refKinds)
    {
        if (refKinds is not null && refKinds.TryGetValue(name, out var kind))
        {
            return kind switch
            {
                RefKind.RemoteBranch => RefBadgeKind.RemoteBranch,
                RefKind.Tag => RefBadgeKind.Tag,
                _ => RefBadgeKind.LocalBranch,
            };
        }

        return name.Contains('/') ? RefBadgeKind.RemoteBranch : RefBadgeKind.LocalBranch;
    }

    private static string GetInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant(),
        };
    }
}

/// <summary>Short, Fork-style age strings ("3 minutes ago", "12 Aug 2025").</summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset value)
    {
        if (value == DateTimeOffset.MinValue)
            return string.Empty;

        var delta = DateTimeOffset.Now - value;
        if (delta < TimeSpan.Zero)
            return "just now";

        return delta switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => Plural((int)delta.TotalMinutes, "minute"),
            { TotalHours: < 24 } => Plural((int)delta.TotalHours, "hour"),
            { TotalDays: < 7 } => Plural((int)delta.TotalDays, "day"),
            { TotalDays: < 30 } => Plural((int)(delta.TotalDays / 7), "week"),
            { TotalDays: < 365 } => Plural((int)(delta.TotalDays / 30), "month"),
            _ => value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture),
        };
    }

    private static string Plural(int count, string unit) =>
        count <= 1 ? $"1 {unit} ago" : $"{count} {unit}s ago";
}
