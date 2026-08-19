namespace GitFork.Core;

/// <summary>A run of text within a diff line, marked as changed or carried over unchanged.</summary>
public sealed record DiffSegment(string Text, bool IsChanged);

/// <summary>
/// Intra-line diffing. Git reports a one-word edit as a whole line removed and a whole line added;
/// this narrows that down to the words that actually differ, which is what makes a large diff
/// readable at a glance.
/// </summary>
public static class WordDiff
{
    /// <summary>
    /// Splits both sides into word tokens and marks the runs that differ. Returns one segment list
    /// per side, each of which concatenates back to the original line.
    /// </summary>
    public static (IReadOnlyList<DiffSegment> Old, IReadOnlyList<DiffSegment> New) Compare(
        string oldLine, string newLine)
    {
        if (oldLine == newLine)
            return ([new DiffSegment(oldLine, false)], [new DiffSegment(newLine, false)]);

        var oldTokens = Tokenize(oldLine);
        var newTokens = Tokenize(newLine);

        var common = LongestCommonSubsequence(oldTokens, newTokens);

        return (BuildSegments(oldTokens, common.Select(p => p.OldIndex).ToHashSet()),
                BuildSegments(newTokens, common.Select(p => p.NewIndex).ToHashSet()));
    }

    /// <summary>
    /// Pairs removed lines with added lines inside one hunk so each pair can be word-diffed.
    /// Only a run of removals immediately followed by a run of additions is treated as a
    /// replacement; anything else is a plain insertion or deletion with nothing to compare against.
    /// </summary>
    public static IReadOnlyDictionary<int, int> PairReplacedLines(DiffHunk hunk)
    {
        var pairs = new Dictionary<int, int>();
        var index = 0;

        while (index < hunk.Lines.Count)
        {
            if (hunk.Lines[index].Kind != DiffLineKind.Removed)
            {
                index++;
                continue;
            }

            var removedStart = index;
            while (index < hunk.Lines.Count && hunk.Lines[index].Kind == DiffLineKind.Removed)
                index++;
            var removedCount = index - removedStart;

            var addedStart = index;
            while (index < hunk.Lines.Count && hunk.Lines[index].Kind == DiffLineKind.Added)
                index++;
            var addedCount = index - addedStart;

            // Pair them off positionally; any surplus on either side has no counterpart.
            for (var offset = 0; offset < Math.Min(removedCount, addedCount); offset++)
                pairs[removedStart + offset] = addedStart + offset;
        }

        return pairs;
    }

    /// <summary>
    /// Splits into word-ish tokens: runs of letters and digits, runs of whitespace, and single
    /// punctuation characters. Keeping punctuation separate stops a changed bracket from marking
    /// the whole expression as changed.
    /// </summary>
    internal static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            var start = index;
            var c = text[index];

            if (char.IsLetterOrDigit(c) || c == '_')
            {
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                    index++;
            }
            else if (char.IsWhiteSpace(c))
            {
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                    index++;
            }
            else
            {
                index++;
            }

            tokens.Add(text[start..index]);
        }

        return tokens;
    }

    /// <summary>
    /// Classic dynamic-programming LCS. Lines are short enough that the quadratic table costs
    /// nothing, and it gives a minimal, stable alignment.
    /// </summary>
    private static List<(int OldIndex, int NewIndex)> LongestCommonSubsequence(
        IReadOnlyList<string> oldTokens, IReadOnlyList<string> newTokens)
    {
        var rows = oldTokens.Count + 1;
        var columns = newTokens.Count + 1;
        var lengths = new int[rows, columns];

        for (var i = oldTokens.Count - 1; i >= 0; i--)
        {
            for (var j = newTokens.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = oldTokens[i] == newTokens[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var matches = new List<(int, int)>();
        var x = 0;
        var y = 0;

        while (x < oldTokens.Count && y < newTokens.Count)
        {
            if (oldTokens[x] == newTokens[y])
            {
                matches.Add((x, y));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return matches;
    }

    /// <summary>Merges adjacent tokens of the same state into as few segments as possible.</summary>
    private static List<DiffSegment> BuildSegments(IReadOnlyList<string> tokens, HashSet<int> unchanged)
    {
        var segments = new List<DiffSegment>();
        var buffer = new System.Text.StringBuilder();
        bool? currentIsChanged = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var isChanged = !unchanged.Contains(i);

            if (currentIsChanged is { } state && state != isChanged)
            {
                segments.Add(new DiffSegment(buffer.ToString(), state));
                buffer.Clear();
            }

            currentIsChanged = isChanged;
            buffer.Append(tokens[i]);
        }

        if (currentIsChanged is { } last)
            segments.Add(new DiffSegment(buffer.ToString(), last));

        return segments;
    }
}
