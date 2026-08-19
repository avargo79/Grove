namespace Grove.Core;

/// <summary>
/// A run of text carrying both of the things a diff line needs to say at once: what kind of code
/// it is (for colour) and whether this is one of the words that actually changed (for highlight).
/// </summary>
public sealed record DiffRun(string Text, TokenKind Token, bool IsWordChanged);

/// <summary>
/// Combines syntax tokens and word-diff segments into a single list of runs.
///
/// Both are partitions of the same line but they split at different places, so the runs are cut at
/// the union of their boundaries — otherwise a changed word that spans a keyword boundary would
/// have to pick one of the two colourings and lose the other.
/// </summary>
public static class DiffRunBuilder
{
    /// <summary>Runs for one line, given its word-level segments and the path it came from.</summary>
    public static IReadOnlyList<DiffRun> Build(
        string text, IReadOnlyList<DiffSegment>? segments, string path)
    {
        if (text.Length == 0)
            return [];

        var tokens = SyntaxHighlighter.Tokenize(text, path);
        var changedRanges = BuildChangedMap(text.Length, segments);

        var runs = new List<DiffRun>();
        var offset = 0;

        foreach (var token in tokens)
        {
            if (token.Text.Length == 0)
                continue;

            // Split this token wherever the changed/unchanged state flips inside it.
            var start = 0;
            while (start < token.Text.Length)
            {
                var state = changedRanges[offset + start];
                var length = 1;
                while (start + length < token.Text.Length &&
                       changedRanges[offset + start + length] == state)
                    length++;

                runs.Add(new DiffRun(token.Text.Substring(start, length), token.Kind, state));
                start += length;
            }

            offset += token.Text.Length;
        }

        return runs;
    }

    /// <summary>Runs with no word-level highlighting, for context lines and whole-line changes.</summary>
    public static IReadOnlyList<DiffRun> BuildPlain(string text, string path, bool allChanged = false)
    {
        if (text.Length == 0)
            return [];

        return
        [
            .. SyntaxHighlighter.Tokenize(text, path)
                .Where(t => t.Text.Length > 0)
                .Select(t => new DiffRun(t.Text, t.Kind, allChanged)),
        ];
    }

    /// <summary>A per-character map of which parts of the line the word diff marked as changed.</summary>
    private static bool[] BuildChangedMap(int length, IReadOnlyList<DiffSegment>? segments)
    {
        var map = new bool[length];
        if (segments is null || segments.Count == 0)
            return map;

        var offset = 0;
        foreach (var segment in segments)
        {
            for (var i = 0; i < segment.Text.Length && offset < length; i++, offset++)
                map[offset] = segment.IsChanged;
        }

        return map;
    }
}
