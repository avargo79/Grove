using GitFork.Core;

namespace GitFork.Core.Tests;

public class SideBySideDiffTests
{
    private static FileDiff Parse(string patch) => DiffParser.ParseFiles(patch)[0];

    private const string ReplacementPatch =
        """
        diff --git a/a.txt b/a.txt
        --- a/a.txt
        +++ b/a.txt
        @@ -1,3 +1,3 @@
         first
        -the quick brown fox
        +the quick red fox
         third
        """;

    [Fact]
    public void EachHunkStartsWithItsHeaderRow()
    {
        var rows = SideBySideDiff.Build(Parse(ReplacementPatch));

        var header = rows[0];
        Assert.True(header.IsHunkHeader);
        Assert.StartsWith("@@ -1,3 +1,3 @@", header.HunkHeader);
        Assert.True(header.Left.IsEmpty);
        Assert.True(header.Right.IsEmpty);
    }

    [Fact]
    public void ContextLinesAppearOnBothSidesWithTheirOwnNumbers()
    {
        var rows = SideBySideDiff.Build(Parse(ReplacementPatch));
        var first = rows.First(r => r.Left.Kind == SideKind.Context);

        Assert.Equal("first", first.Left.Text);
        Assert.Equal("first", first.Right.Text);
        Assert.Equal(1, first.Left.LineNumber);
        Assert.Equal(1, first.Right.LineNumber);
    }

    [Fact]
    public void AReplacementIsPairedOntoOneRow()
    {
        var rows = SideBySideDiff.Build(Parse(ReplacementPatch));
        var row = rows.Single(r => r.Left.Kind == SideKind.Removed);

        Assert.Equal(SideKind.Added, row.Right.Kind);
        Assert.Equal("the quick brown fox", row.Left.Text);
        Assert.Equal("the quick red fox", row.Right.Text);
    }

    [Fact]
    public void APairedRowCarriesWordLevelSegments()
    {
        var rows = SideBySideDiff.Build(Parse(ReplacementPatch));
        var row = rows.Single(r => r.Left.Kind == SideKind.Removed);

        Assert.Equal("brown", string.Concat(row.Left.Segments.Where(s => s.IsChanged).Select(s => s.Text)));
        Assert.Equal("red", string.Concat(row.Right.Segments.Where(s => s.IsChanged).Select(s => s.Text)));
    }

    [Fact]
    public void WordLevelSegmentsCanBeTurnedOff()
    {
        var rows = SideBySideDiff.Build(Parse(ReplacementPatch), wordLevel: false);
        var row = rows.Single(r => r.Left.Kind == SideKind.Removed);

        // The whole line reads as changed instead.
        Assert.Equal("the quick brown fox", string.Concat(
            row.Left.Segments.Where(s => s.IsChanged).Select(s => s.Text)));
    }

    [Fact]
    public void APureInsertionLeavesTheLeftSideEmpty()
    {
        var rows = SideBySideDiff.Build(Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,2 @@
             kept
            +added
            """));

        var row = rows.Single(r => r.Right.Kind == SideKind.Added);
        Assert.True(row.Left.IsEmpty);
        Assert.Equal("added", row.Right.Text);
    }

    [Fact]
    public void APureDeletionLeavesTheRightSideEmpty()
    {
        var rows = SideBySideDiff.Build(Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,1 @@
             kept
            -removed
            """));

        var row = rows.Single(r => r.Left.Kind == SideKind.Removed);
        Assert.True(row.Right.IsEmpty);
        Assert.Equal("removed", row.Left.Text);
    }

    [Fact]
    public void UnevenRunsPadTheShorterSide()
    {
        var rows = SideBySideDiff.Build(Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,2 @@
            -one
            -two
            -three
            +uno
            """));

        var changed = rows.Where(r => !r.IsHunkHeader).ToList();

        // Three removals against one addition: three rows, two with an empty right side.
        Assert.Equal(3, changed.Count);
        Assert.Equal("uno", changed[0].Right.Text);
        Assert.True(changed[1].Right.IsEmpty);
        Assert.True(changed[2].Right.IsEmpty);
    }

    [Fact]
    public void TheTwoColumnsNeverDriftOutOfStep()
    {
        var rows = SideBySideDiff.Build(Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,6 +1,7 @@
             context one
            -removed one
            -removed two
            +added one
             context two
            +added two
            +added three
             context three
            """));

        // Every row has at least one populated side; an all-empty row would be a gap on screen.
        Assert.All(rows.Where(r => !r.IsHunkHeader),
            r => Assert.False(r.Left.IsEmpty && r.Right.IsEmpty));
    }

    [Fact]
    public void EveryHunkGetsItsOwnHeaderRow()
    {
        var rows = SideBySideDiff.Build(Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
            -one
            +uno
            @@ -50,2 +50,2 @@
            -fifty
            +cincuenta
            """));

        Assert.Equal(2, rows.Count(r => r.IsHunkHeader));
    }

    [Fact]
    public void NoNewlineMarkersAreLeftOutOfTheTwoColumnView()
    {
        var rows = SideBySideDiff.Build(Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -old
            \ No newline at end of file
            +new
            """));

        Assert.DoesNotContain(rows, r => r.Left.Text.StartsWith('\\') || r.Right.Text.StartsWith('\\'));
    }

    [Fact]
    public void AnEmptyDiffProducesNoRows()
    {
        Assert.Empty(SideBySideDiff.Build(new FileDiff
        {
            HeaderLines = [],
            Hunks = [],
            Path = "a.txt",
        }));
    }
}
