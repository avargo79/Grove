using System.Text;

namespace GitGui.Core;

/// <summary>Which way the generated patch will be applied.</summary>
public enum PatchDirection
{
    /// <summary>Applied forward with <c>git apply --cached</c> to stage the selection.</summary>
    Stage,

    /// <summary>Applied with <c>git apply --cached --reverse</c> to unstage the selection.</summary>
    Unstage,
}

/// <summary>
/// Synthesises a unified patch covering only a chosen subset of a file's hunks or lines, which is
/// how hunk- and line-level staging works: git itself has no "stage these lines" command, so the
/// selection is expressed as a patch and fed to <c>git apply --cached</c>.
/// </summary>
public static class PatchBuilder
{
    /// <summary>Patch containing whole hunks, selected by their index within the file.</summary>
    public static string? BuildHunkPatch(FileDiff file, IEnumerable<int> hunkIndices, PatchDirection direction)
    {
        var selection = new Dictionary<int, IReadOnlySet<int>>();
        foreach (var index in hunkIndices)
        {
            if (index < 0 || index >= file.Hunks.Count)
                continue;
            selection[index] = file.Hunks[index].ChangeLineIndices.ToHashSet();
        }

        return BuildSelectionPatch(file, selection, direction);
    }

    /// <summary>Patch containing every change in the file.</summary>
    public static string? BuildWholeFilePatch(FileDiff file, PatchDirection direction) =>
        BuildHunkPatch(file, Enumerable.Range(0, file.Hunks.Count), direction);

    /// <summary>
    /// Patch containing only the selected lines, keyed by hunk index then by line index within
    /// that hunk. Returns null when the selection contains nothing applicable.
    /// </summary>
    public static string? BuildSelectionPatch(
        FileDiff file,
        IReadOnlyDictionary<int, IReadOnlySet<int>> selectedLinesByHunk,
        PatchDirection direction)
    {
        if (file.IsBinary)
            return null;

        var body = new StringBuilder();
        var emittedHunk = false;

        // Each emitted hunk shifts the far side's line numbers relative to the anchoring side.
        var delta = 0;

        for (var hunkIndex = 0; hunkIndex < file.Hunks.Count; hunkIndex++)
        {
            if (!selectedLinesByHunk.TryGetValue(hunkIndex, out var selected) || selected.Count == 0)
                continue;

            var rendered = RenderHunk(file.Hunks[hunkIndex], selected, direction, delta);
            if (rendered is null)
                continue;

            body.Append(rendered.Value.Text);
            delta += rendered.Value.Delta;
            emittedHunk = true;
        }

        if (!emittedHunk)
            return null;

        var patch = new StringBuilder();
        foreach (var header in file.HeaderLines)
            patch.Append(header).Append('\n');
        patch.Append(body);
        return patch.ToString();
    }

    /// <summary>
    /// Rewrites one hunk down to the selected lines.
    ///
    /// Staging applies the patch forward, so the file on disk is the "new" side and the index is
    /// the "old" side: an unselected addition is simply left out, while an unselected removal has
    /// to survive as context because that line stays in the index.
    ///
    /// Unstaging reverse-applies against the index, so the roles swap and so do the rules.
    /// </summary>
    private static (string Text, int Delta)? RenderHunk(
        DiffHunk hunk, IReadOnlySet<int> selected, PatchDirection direction, int delta)
    {
        var lines = new List<string>();
        int oldCount = 0, newCount = 0, changes = 0;

        // True when the previous source line survived, so a following no-newline marker applies.
        var lastLineEmitted = false;

        for (var i = 0; i < hunk.Lines.Count; i++)
        {
            var line = hunk.Lines[i];

            switch (line.Kind)
            {
                case DiffLineKind.Context:
                    lines.Add(" " + line.Text);
                    oldCount++;
                    newCount++;
                    lastLineEmitted = true;
                    break;

                case DiffLineKind.Added when selected.Contains(i):
                    lines.Add("+" + line.Text);
                    newCount++;
                    changes++;
                    lastLineEmitted = true;
                    break;

                case DiffLineKind.Added:
                    if (direction == PatchDirection.Stage)
                    {
                        // Not being staged, and not present in the index: leave it out entirely.
                        lastLineEmitted = false;
                    }
                    else
                    {
                        // Staying staged, so it must stay in the side we are matching against.
                        lines.Add(" " + line.Text);
                        oldCount++;
                        newCount++;
                        lastLineEmitted = true;
                    }
                    break;

                case DiffLineKind.Removed when selected.Contains(i):
                    lines.Add("-" + line.Text);
                    oldCount++;
                    changes++;
                    lastLineEmitted = true;
                    break;

                case DiffLineKind.Removed:
                    if (direction == PatchDirection.Stage)
                    {
                        // Not being staged, so the line stays in the index: carry it as context.
                        lines.Add(" " + line.Text);
                        oldCount++;
                        newCount++;
                        lastLineEmitted = true;
                    }
                    else
                    {
                        // Already absent from the index, which is the side being matched.
                        lastLineEmitted = false;
                    }
                    break;

                case DiffLineKind.NoNewline:
                    // Only meaningful when the line it qualifies survived the rewrite.
                    if (lastLineEmitted)
                        lines.Add(line.Text);
                    break;

                default:
                    break;
            }
        }

        if (changes == 0)
            return null;

        // The anchoring side keeps git's original numbering; the other side shifts by everything
        // already emitted into this patch.
        var (oldStart, newStart) = direction == PatchDirection.Stage
            ? (hunk.OldStart, hunk.OldStart + delta)
            : (hunk.NewStart + delta, hunk.NewStart);

        var text = new StringBuilder();
        text.Append(FormatHeader(oldStart, oldCount, newStart, newCount, SectionHeading(hunk.Header)))
            .Append('\n');
        foreach (var line in lines)
            text.Append(line).Append('\n');

        var hunkDelta = direction == PatchDirection.Stage
            ? newCount - oldCount
            : oldCount - newCount;

        return (text.ToString(), hunkDelta);
    }

    /// <summary>An empty range is written as start 0, which is what git emits for new/deleted files.</summary>
    private static string FormatHeader(int oldStart, int oldCount, int newStart, int newCount, string heading)
    {
        if (oldCount == 0)
            oldStart = 0;
        if (newCount == 0)
            newStart = 0;

        var header = $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@";
        return heading.Length > 0 ? header + heading : header;
    }

    /// <summary>Keeps the trailing function-name heading git puts after the second "@@".</summary>
    private static string SectionHeading(string header)
    {
        var marker = header.IndexOf("@@", StringComparison.Ordinal);
        if (marker < 0)
            return string.Empty;
        var second = header.IndexOf("@@", marker + 2, StringComparison.Ordinal);
        return second < 0 ? string.Empty : header[(second + 2)..];
    }
}
