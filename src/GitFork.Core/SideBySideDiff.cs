namespace GitFork.Core;

/// <summary>What occupies one side of a side-by-side row.</summary>
public enum SideKind
{
    /// <summary>Nothing — the other side has a line here and this one does not.</summary>
    Empty,
    Context,
    Removed,
    Added,
}

/// <summary>One side of one row: its text, line number, and word-level segments.</summary>
public sealed record SideBySideCell(
    SideKind Kind,
    string Text,
    int? LineNumber,
    IReadOnlyList<DiffSegment> Segments)
{
    public static SideBySideCell Empty { get; } = new(SideKind.Empty, string.Empty, null, []);

    public bool IsEmpty => Kind == SideKind.Empty;
}

/// <summary>A single row of the two-column view.</summary>
public sealed record SideBySideRow(SideBySideCell Left, SideBySideCell Right)
{
    /// <summary>True for a row that only marks the start of a hunk.</summary>
    public bool IsHunkHeader { get; init; }

    public string HunkHeader { get; init; } = string.Empty;
}

/// <summary>
/// Lays a unified diff out as two aligned columns. Removals and additions that sit next to each
/// other are paired onto the same row so a replacement reads across, and the shorter side is
/// padded so the columns never drift out of step.
/// </summary>
public static class SideBySideDiff
{
    public static IReadOnlyList<SideBySideRow> Build(FileDiff file, bool wordLevel = true)
    {
        var rows = new List<SideBySideRow>();

        foreach (var hunk in file.Hunks)
        {
            rows.Add(new SideBySideRow(SideBySideCell.Empty, SideBySideCell.Empty)
            {
                IsHunkHeader = true,
                HunkHeader = hunk.Header,
            });

            rows.AddRange(BuildHunk(hunk, wordLevel));
        }

        return rows;
    }

    private static List<SideBySideRow> BuildHunk(DiffHunk hunk, bool wordLevel)
    {
        var rows = new List<SideBySideRow>();
        var index = 0;

        while (index < hunk.Lines.Count)
        {
            var line = hunk.Lines[index];

            switch (line.Kind)
            {
                case DiffLineKind.Context:
                    rows.Add(new SideBySideRow(
                        new SideBySideCell(SideKind.Context, line.Text, line.OldLineNumber, Plain(line.Text)),
                        new SideBySideCell(SideKind.Context, line.Text, line.NewLineNumber, Plain(line.Text))));
                    index++;
                    break;

                case DiffLineKind.Removed:
                case DiffLineKind.Added:
                {
                    // Gather the whole removed run and the added run that follows it.
                    var removed = new List<DiffLine>();
                    while (index < hunk.Lines.Count && hunk.Lines[index].Kind == DiffLineKind.Removed)
                        removed.Add(hunk.Lines[index++]);

                    var added = new List<DiffLine>();
                    while (index < hunk.Lines.Count && hunk.Lines[index].Kind == DiffLineKind.Added)
                        added.Add(hunk.Lines[index++]);

                    rows.AddRange(PairRuns(removed, added, wordLevel));
                    break;
                }

                default:
                    // No-newline markers and stray headers have no place in a two-column view.
                    index++;
                    break;
            }
        }

        return rows;
    }

    /// <summary>
    /// Puts each removal opposite its replacement, then pads whichever side ran out. Only paired
    /// rows get word-level segments: an unpaired line has nothing to be compared against.
    /// </summary>
    private static List<SideBySideRow> PairRuns(
        List<DiffLine> removed, List<DiffLine> added, bool wordLevel)
    {
        var rows = new List<SideBySideRow>();
        var count = Math.Max(removed.Count, added.Count);

        for (var i = 0; i < count; i++)
        {
            var oldLine = i < removed.Count ? removed[i] : null;
            var newLine = i < added.Count ? added[i] : null;

            IReadOnlyList<DiffSegment> oldSegments;
            IReadOnlyList<DiffSegment> newSegments;

            if (wordLevel && oldLine is not null && newLine is not null)
            {
                (oldSegments, newSegments) = WordDiff.Compare(oldLine.Text, newLine.Text);
            }
            else
            {
                oldSegments = oldLine is null ? [] : Changed(oldLine.Text);
                newSegments = newLine is null ? [] : Changed(newLine.Text);
            }

            rows.Add(new SideBySideRow(
                oldLine is null
                    ? SideBySideCell.Empty
                    : new SideBySideCell(SideKind.Removed, oldLine.Text, oldLine.OldLineNumber, oldSegments),
                newLine is null
                    ? SideBySideCell.Empty
                    : new SideBySideCell(SideKind.Added, newLine.Text, newLine.NewLineNumber, newSegments)));
        }

        return rows;
    }

    private static IReadOnlyList<DiffSegment> Plain(string text) => [new DiffSegment(text, false)];

    private static IReadOnlyList<DiffSegment> Changed(string text) => [new DiffSegment(text, true)];
}
