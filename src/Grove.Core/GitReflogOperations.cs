using System.Globalization;

namespace Grove.Core;

/// <summary>
/// One reflog entry: where a ref pointed, when, and what moved it. This is how a commit orphaned
/// by a reset or a bad rebase is found again.
/// </summary>
public sealed record ReflogEntry(
    string Selector,
    string Sha,
    string Action,
    string Subject,
    DateTimeOffset Date)
{
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;

    /// <summary>
    /// True for entries that discarded commits. These are the ones worth looking at when something
    /// has gone missing.
    /// </summary>
    public bool IsPotentiallyDestructive =>
        Action.StartsWith("reset", StringComparison.OrdinalIgnoreCase) ||
        Action.StartsWith("rebase", StringComparison.OrdinalIgnoreCase) ||
        Action.Contains("amend", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The reflog, for recovering work that is no longer reachable from any branch.</summary>
public sealed class GitReflogOperations(GitCommandRunner git)
{
    private const char Sep = '\u001F';

    /// <summary>Reflog entries newest first, for HEAD or for a specific ref.</summary>
    public async Task<IReadOnlyList<ReflogEntry>> GetEntriesAsync(
        string reference = "HEAD", int maxCount = 200, CancellationToken ct = default)
    {
        var format = string.Join(Sep, "%gD", "%H", "%gs", "%aI");
        var result = await git
            .RunAsync(ct, "reflog", "--no-abbrev", $"--format={format}", $"--max-count={maxCount}", reference)
            .ConfigureAwait(false);

        if (!result.Success)
            return [];

        var entries = new List<ReflogEntry>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(Sep);
            if (f.Length < 4)
                continue;

            var (action, subject) = SplitAction(f[2]);
            entries.Add(new ReflogEntry(
                Selector: f[0],
                Sha: f[1],
                Action: action,
                Subject: subject,
                Date: DateTimeOffset.TryParse(f[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date
                    : DateTimeOffset.MinValue));
        }

        return entries;
    }

    /// <summary>
    /// Commits in the reflog that no branch or tag can reach any more — what you are actually
    /// looking for after losing work.
    /// </summary>
    public async Task<IReadOnlyList<ReflogEntry>> GetUnreachableEntriesAsync(
        int maxCount = 200, CancellationToken ct = default)
    {
        var entries = await GetEntriesAsync("HEAD", maxCount, ct).ConfigureAwait(false);
        if (entries.Count == 0)
            return [];

        // One call rather than one per entry: ask which of these shas are reachable from any ref.
        var reachable = await GetReachableShasAsync(ct).ConfigureAwait(false);
        return [.. entries.Where(e => !reachable.Contains(e.Sha))];
    }

    private async Task<HashSet<string>> GetReachableShasAsync(CancellationToken ct)
    {
        var result = await git
            .RunAsync(ct, "rev-list", "--all", "--max-count=5000")
            .ConfigureAwait(false);

        return result.Success
            ? [.. result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())]
            : [];
    }

    /// <summary>
    /// Splits git's reflog subject, which reads "action: detail" — "commit: fix the thing",
    /// "reset: moving to HEAD~1", "checkout: moving from main to feature".
    /// </summary>
    internal static (string Action, string Subject) SplitAction(string reflogSubject)
    {
        var colon = reflogSubject.IndexOf(':');
        return colon < 0
            ? (reflogSubject, string.Empty)
            : (reflogSubject[..colon], reflogSubject[(colon + 1)..].Trim());
    }
}
