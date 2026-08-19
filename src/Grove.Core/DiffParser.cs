namespace Grove.Core;

/// <summary>One hunk of a unified diff, with its ranges and lines kept together.</summary>
public sealed class DiffHunk
{
    /// <summary>The literal <c>@@ … @@</c> line, section heading included.</summary>
    public required string Header { get; init; }

    public required int OldStart { get; init; }
    public required int OldCount { get; init; }
    public required int NewStart { get; init; }
    public required int NewCount { get; init; }

    /// <summary>Body lines only: context, additions, removals and no-newline markers.</summary>
    public required IReadOnlyList<DiffLine> Lines { get; init; }

    /// <summary>Indices into <see cref="Lines"/> that can be staged or unstaged individually.</summary>
    public IEnumerable<int> ChangeLineIndices =>
        Lines.Index().Where(x => x.Item.Kind is DiffLineKind.Added or DiffLineKind.Removed).Select(x => x.Index);

    public bool HasChanges => Lines.Any(l => l.Kind is DiffLineKind.Added or DiffLineKind.Removed);
}

/// <summary>A single file's diff: the <c>diff --git</c> preamble plus its hunks.</summary>
public sealed class FileDiff
{
    /// <summary>Everything before the first hunk — the git header, mode changes, ---/+++ lines.</summary>
    public required IReadOnlyList<string> HeaderLines { get; init; }

    public required IReadOnlyList<DiffHunk> Hunks { get; init; }

    /// <summary>Path on the "b" side, or the "a" side for a deletion.</summary>
    public required string Path { get; init; }

    /// <summary>True when git reported the file as binary rather than emitting hunks.</summary>
    public bool IsBinary { get; init; }

    /// <summary>Flattens back to a display list, header lines included.</summary>
    public IReadOnlyList<DiffLine> ToLines()
    {
        var lines = new List<DiffLine>();
        foreach (var header in HeaderLines)
            lines.Add(new DiffLine(DiffLineKind.Header, header, null, null));

        foreach (var hunk in Hunks)
        {
            lines.Add(new DiffLine(DiffLineKind.HunkHeader, hunk.Header, null, null));
            lines.AddRange(hunk.Lines);
        }

        return lines;
    }
}

/// <summary>Turns a unified diff into typed lines with old/new line numbers attached.</summary>
public static class DiffParser
{
    /// <summary>Flat line list across every file in the patch, for read-only display.</summary>
    public static IReadOnlyList<DiffLine> Parse(string patch) =>
        [.. ParseFiles(patch).SelectMany(f => f.ToLines())];

    /// <summary>Structured parse: one <see cref="FileDiff"/> per file, each with its hunks.</summary>
    public static IReadOnlyList<FileDiff> ParseFiles(string patch)
    {
        var files = new List<FileDiff>();
        if (string.IsNullOrEmpty(patch))
            return files;

        var headerLines = new List<string>();
        var hunks = new List<DiffHunk>();
        var hunkLines = new List<DiffLine>();

        string? hunkHeader = null;
        int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0;
        int oldLine = 0, newLine = 0;
        var isBinary = false;

        void FlushHunk()
        {
            if (hunkHeader is null)
                return;
            hunks.Add(new DiffHunk
            {
                Header = hunkHeader,
                OldStart = oldStart,
                OldCount = oldCount,
                NewStart = newStart,
                NewCount = newCount,
                Lines = [.. hunkLines],
            });
            hunkHeader = null;
            hunkLines.Clear();
        }

        void FlushFile()
        {
            FlushHunk();
            if (headerLines.Count == 0 && hunks.Count == 0)
                return;
            files.Add(new FileDiff
            {
                HeaderLines = [.. headerLines],
                Hunks = [.. hunks],
                Path = ExtractPath(headerLines),
                IsBinary = isBinary,
            });
            headerLines.Clear();
            hunks.Clear();
            isBinary = false;
        }

        foreach (var raw in patch.Split('\n'))
        {
            var text = raw.TrimEnd('\r');

            if (text.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushFile();
                headerLines.Add(text);
                continue;
            }

            if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushHunk();
                hunkHeader = text;
                (oldStart, oldCount, newStart, newCount) = ParseHunkRanges(text);
                oldLine = oldStart;
                newLine = newStart;
                continue;
            }

            if (hunkHeader is null)
            {
                // Everything before the first hunk is metadata: the file header, mode
                // changes, similarity scores and binary notices.
                if (text.Length == 0)
                    continue;
                if (text.StartsWith("Binary files ", StringComparison.Ordinal) ||
                    text.StartsWith("GIT binary patch", StringComparison.Ordinal))
                    isBinary = true;
                headerLines.Add(text);
                continue;
            }

            if (text.StartsWith('\\')) // "\ No newline at end of file"
            {
                hunkLines.Add(new DiffLine(DiffLineKind.NoNewline, text, null, null));
                continue;
            }

            // A real context blank line is " "; a zero-length line is the trailing split artefact.
            if (text.Length == 0)
                continue;

            switch (text[0])
            {
                case '+':
                    hunkLines.Add(new DiffLine(DiffLineKind.Added, text[1..], null, newLine++));
                    break;
                case '-':
                    hunkLines.Add(new DiffLine(DiffLineKind.Removed, text[1..], oldLine++, null));
                    break;
                case ' ':
                    hunkLines.Add(new DiffLine(DiffLineKind.Context, text[1..], oldLine++, newLine++));
                    break;
                default:
                    // Anything else ends the hunk body (e.g. a trailing "-- " signature line).
                    FlushHunk();
                    headerLines.Add(text);
                    break;
            }
        }

        FlushFile();
        return files;
    }

    /// <summary>Reads the "b/" path out of the file header, falling back to the "a/" side.</summary>
    private static string ExtractPath(IReadOnlyList<string> headerLines)
    {
        foreach (var line in headerLines)
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
                return line["+++ b/".Length..];
            if (line.StartsWith("+++ ", StringComparison.Ordinal) && !line.EndsWith("/dev/null", StringComparison.Ordinal))
                return line["+++ ".Length..];
        }

        var oldSide = headerLines.FirstOrDefault(l => l.StartsWith("--- a/", StringComparison.Ordinal));
        return oldSide is null ? string.Empty : oldSide["--- a/".Length..];
    }

    /// <summary>Reads the starting line numbers out of a hunk header like "@@ -12,7 +12,9 @@".</summary>
    internal static (int OldStart, int NewStart) ParseHunkHeader(string header)
    {
        var (oldStart, _, newStart, _) = ParseHunkRanges(header);
        return (oldStart, newStart);
    }

    /// <summary>Reads both ranges; an omitted count means 1, as the unified format specifies.</summary>
    internal static (int OldStart, int OldCount, int NewStart, int NewCount) ParseHunkRanges(string header)
    {
        var (oldStart, oldCount) = ReadRange(header, '-');
        var (newStart, newCount) = ReadRange(header, '+');
        return (oldStart, oldCount, newStart, newCount);

        static (int Start, int Count) ReadRange(string s, char marker)
        {
            var index = s.IndexOf(marker);
            if (index < 0)
                return (0, 0);

            var start = ReadNumber(s, index + 1, out var next);
            if (next < s.Length && s[next] == ',')
                return (start, ReadNumber(s, next + 1, out _));

            // "@@ -1 +1 @@" omits the count, which the format defines as 1.
            return (start, 1);
        }

        static int ReadNumber(string s, int start, out int end)
        {
            end = start;
            while (end < s.Length && char.IsAsciiDigit(s[end]))
                end++;
            return end > start && int.TryParse(s.AsSpan(start, end - start), out var value) ? value : 0;
        }
    }
}
