namespace GitFork.Core;

/// <summary>Turns a unified diff into typed lines with old/new line numbers attached.</summary>
public static class DiffParser
{
    public static IReadOnlyList<DiffLine> Parse(string patch)
    {
        var lines = new List<DiffLine>();
        if (string.IsNullOrEmpty(patch))
            return lines;

        int oldLine = 0, newLine = 0;
        var inHunk = false;

        foreach (var raw in patch.Split('\n'))
        {
            var text = raw.TrimEnd('\r');

            if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldLine, newLine) = ParseHunkHeader(text);
                inHunk = true;
                lines.Add(new DiffLine(DiffLineKind.HunkHeader, text, null, null));
                continue;
            }

            if (!inHunk)
            {
                // Everything before the first hunk is metadata: the file header, mode
                // changes, similarity scores and binary notices.
                if (text.Length > 0)
                    lines.Add(new DiffLine(DiffLineKind.Header, text, null, null));
                continue;
            }

            if (text.StartsWith('\\')) // "\ No newline at end of file"
            {
                lines.Add(new DiffLine(DiffLineKind.NoNewline, text, null, null));
                continue;
            }

            // A real context blank line is " "; a zero-length line is the trailing split artefact.
            if (text.Length == 0)
                continue;

            switch (text[0])
            {
                case '+':
                    lines.Add(new DiffLine(DiffLineKind.Added, text[1..], null, newLine++));
                    break;
                case '-':
                    lines.Add(new DiffLine(DiffLineKind.Removed, text[1..], oldLine++, null));
                    break;
                case ' ':
                    lines.Add(new DiffLine(DiffLineKind.Context, text[1..], oldLine++, newLine++));
                    break;
                default:
                    lines.Add(new DiffLine(DiffLineKind.Header, text, null, null));
                    break;
            }
        }

        return lines;
    }

    /// <summary>Reads the starting line numbers out of a hunk header like "@@ -12,7 +12,9 @@".</summary>
    internal static (int OldStart, int NewStart) ParseHunkHeader(string header)
    {
        int oldStart = 0, newStart = 0;

        var minus = header.IndexOf('-');
        if (minus >= 0)
            oldStart = ReadNumber(header, minus + 1);

        var plus = header.IndexOf('+');
        if (plus >= 0)
            newStart = ReadNumber(header, plus + 1);

        return (oldStart, newStart);

        static int ReadNumber(string s, int start)
        {
            var end = start;
            while (end < s.Length && char.IsAsciiDigit(s[end]))
                end++;
            return end > start && int.TryParse(s.AsSpan(start, end - start), out var value) ? value : 0;
        }
    }
}
