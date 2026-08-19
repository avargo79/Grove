using System.Globalization;

namespace GitGui.Core;

/// <summary>
/// What to narrow the history down to. Every field is optional; an empty filter means the whole
/// history, which is the normal case.
/// </summary>
public sealed record CommitFilter
{
    public static CommitFilter Empty { get; } = new();

    /// <summary>Matched against commit messages.</summary>
    public string? Text { get; init; }

    /// <summary>Matched against the author's name and email.</summary>
    public string? Author { get; init; }

    /// <summary>Only commits touching this path.</summary>
    public string? Path { get; init; }

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }

    /// <summary>
    /// True to require every criterion to match the same commit. Git's default is to OR the
    /// message and author greps together, which is almost never what a search box means.
    /// </summary>
    public bool MatchAll { get; init; } = true;

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Text) &&
        string.IsNullOrWhiteSpace(Author) &&
        string.IsNullOrWhiteSpace(Path) &&
        Since is null &&
        Until is null;

    /// <summary>The git arguments this filter corresponds to, excluding any path separator.</summary>
    public IEnumerable<string> ToArguments()
    {
        if (!string.IsNullOrWhiteSpace(Text))
        {
            yield return "--regexp-ignore-case";
            yield return "--fixed-strings";
            yield return $"--grep={Text}";
        }

        if (!string.IsNullOrWhiteSpace(Author))
        {
            yield return "--regexp-ignore-case";
            yield return $"--author={Author}";
        }

        // Only meaningful when there is more than one grep-style criterion to combine.
        if (MatchAll && !string.IsNullOrWhiteSpace(Text) && !string.IsNullOrWhiteSpace(Author))
            yield return "--all-match";

        if (Since is { } since)
            yield return $"--since={since.ToString("o", CultureInfo.InvariantCulture)}";

        if (Until is { } until)
            yield return $"--until={until.ToString("o", CultureInfo.InvariantCulture)}";
    }

    /// <summary>A short description of what is being filtered, for the UI to show.</summary>
    public string Describe()
    {
        if (IsEmpty)
            return string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Text))
            parts.Add($"message contains \"{Text}\"");
        if (!string.IsNullOrWhiteSpace(Author))
            parts.Add($"author matches \"{Author}\"");
        if (!string.IsNullOrWhiteSpace(Path))
            parts.Add($"touches {Path}");
        if (Since is { } since)
            parts.Add($"since {since.ToLocalTime():d MMM yyyy}");
        if (Until is { } until)
            parts.Add($"until {until.ToLocalTime():d MMM yyyy}");

        return string.Join(", ", parts);
    }
}

/// <summary>One page of history, and whether asking for more would return anything.</summary>
public sealed record CommitPage(IReadOnlyList<Commit> Commits, bool HasMore, int Skipped)
{
    public static CommitPage Empty { get; } = new([], false, 0);

    /// <summary>How many commits have been loaded in total up to the end of this page.</summary>
    public int LoadedCount => Skipped + Commits.Count;
}
