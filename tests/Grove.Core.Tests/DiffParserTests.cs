using Grove.Core;

namespace Grove.Core.Tests;

public class DiffParserTests
{
    private const string SimplePatch =
        """
        diff --git a/src/app.cs b/src/app.cs
        index 1234567..89abcde 100644
        --- a/src/app.cs
        +++ b/src/app.cs
        @@ -10,6 +10,7 @@ public class App
             public void Run()
             {
        -        Console.WriteLine("old");
        +        Console.WriteLine("new");
        +        Console.WriteLine("extra");
             }
         }
        """;

    [Fact]
    public void EmptyPatchProducesNoLines()
    {
        Assert.Empty(DiffParser.Parse(string.Empty));
        Assert.Empty(DiffParser.Parse(null!));
    }

    [Fact]
    public void HeaderLinesBeforeTheFirstHunkAreClassifiedAsHeaders()
    {
        var lines = DiffParser.Parse(SimplePatch);
        var headers = lines.TakeWhile(l => l.Kind != DiffLineKind.HunkHeader).ToList();

        Assert.Equal(4, headers.Count);
        Assert.All(headers, l => Assert.Equal(DiffLineKind.Header, l.Kind));
        Assert.StartsWith("diff --git", headers[0].Text);
    }

    [Fact]
    public void HunkHeaderIsKeptVerbatim()
    {
        var hunk = DiffParser.Parse(SimplePatch).Single(l => l.Kind == DiffLineKind.HunkHeader);

        Assert.StartsWith("@@ -10,6 +10,7 @@", hunk.Text);
        Assert.Null(hunk.OldLineNumber);
        Assert.Null(hunk.NewLineNumber);
    }

    [Fact]
    public void LeadingMarkerIsStrippedFromLineText()
    {
        var lines = DiffParser.Parse(SimplePatch);

        var added = lines.Where(l => l.Kind == DiffLineKind.Added).ToList();
        Assert.Equal(2, added.Count);
        Assert.Contains("Console.WriteLine(\"new\");", added[0].Text);
        Assert.DoesNotContain('+', added[0].Text);

        var removed = Assert.Single(lines, l => l.Kind == DiffLineKind.Removed);
        Assert.Contains("Console.WriteLine(\"old\");", removed.Text);
    }

    [Fact]
    public void ContextLinesAdvanceBothSideCounters()
    {
        var lines = DiffParser.Parse(SimplePatch);
        var context = lines.Where(l => l.Kind == DiffLineKind.Context).ToList();

        // The hunk starts at old line 10 / new line 10.
        Assert.Equal(10, context[0].OldLineNumber);
        Assert.Equal(10, context[0].NewLineNumber);
        Assert.Equal(11, context[1].OldLineNumber);
        Assert.Equal(11, context[1].NewLineNumber);
    }

    [Fact]
    public void RemovedLinesNumberTheOldSideOnly()
    {
        var removed = Assert.Single(DiffParser.Parse(SimplePatch), l => l.Kind == DiffLineKind.Removed);

        Assert.Equal(12, removed.OldLineNumber);
        Assert.Null(removed.NewLineNumber);
    }

    [Fact]
    public void AddedLinesNumberTheNewSideOnly()
    {
        var added = DiffParser.Parse(SimplePatch).Where(l => l.Kind == DiffLineKind.Added).ToList();

        Assert.Equal(12, added[0].NewLineNumber);
        Assert.Equal(13, added[1].NewLineNumber);
        Assert.All(added, l => Assert.Null(l.OldLineNumber));
    }

    [Fact]
    public void CountersContinuePastTheChangedBlock()
    {
        var lines = DiffParser.Parse(SimplePatch);
        var trailing = lines.Where(l => l.Kind == DiffLineKind.Context).ToList();

        // After one removal and two additions the two sides are offset by one.
        Assert.Equal(13, trailing[^2].OldLineNumber);
        Assert.Equal(14, trailing[^2].NewLineNumber);
    }

    [Fact]
    public void MultipleHunksResetTheCountersToEachHeader()
    {
        const string patch =
            """
            @@ -1,2 +1,2 @@
            -first
            +First
             second
            @@ -50,2 +50,2 @@
            -fiftieth
            +Fiftieth
             fifty-first
            """;

        var lines = DiffParser.Parse(patch);
        var removed = lines.Where(l => l.Kind == DiffLineKind.Removed).ToList();

        Assert.Equal(1, removed[0].OldLineNumber);
        Assert.Equal(50, removed[1].OldLineNumber);
    }

    [Fact]
    public void BlankContextLineIsPreservedAsAnEmptyContextLine()
    {
        // A blank source line arrives as a single space; it must not be dropped or renumbered away.
        var patch = "@@ -1,3 +1,3 @@\n a\n \n b\n";
        var lines = DiffParser.Parse(patch);
        var context = lines.Where(l => l.Kind == DiffLineKind.Context).ToList();

        Assert.Equal(3, context.Count);
        Assert.Equal(string.Empty, context[1].Text);
        Assert.Equal(2, context[1].OldLineNumber);
    }

    [Fact]
    public void NoNewlineMarkerIsItsOwnKindAndDoesNotConsumeALineNumber()
    {
        const string patch =
            """
            @@ -1,1 +1,1 @@
            -old
            \ No newline at end of file
            +new
            """;

        var lines = DiffParser.Parse(patch);

        var marker = Assert.Single(lines, l => l.Kind == DiffLineKind.NoNewline);
        Assert.Null(marker.OldLineNumber);
        Assert.Null(marker.NewLineNumber);
        Assert.Equal(1, lines.Single(l => l.Kind == DiffLineKind.Added).NewLineNumber);
    }

    [Fact]
    public void RenameHeadersAreReportedWithoutAnyHunk()
    {
        const string patch =
            """
            diff --git a/old/name.cs b/new/name.cs
            similarity index 96%
            rename from old/name.cs
            rename to new/name.cs
            """;

        var lines = DiffParser.Parse(patch);

        Assert.All(lines, l => Assert.Equal(DiffLineKind.Header, l.Kind));
        Assert.Contains(lines, l => l.Text == "rename from old/name.cs");
    }

    [Fact]
    public void BinaryFileNoticeIsReportedAsAHeader()
    {
        const string patch =
            """
            diff --git a/logo.png b/logo.png
            Binary files a/logo.png and b/logo.png differ
            """;

        var lines = DiffParser.Parse(patch);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(DiffLineKind.Header, l.Kind));
    }

    [Fact]
    public void CarriageReturnsAreTrimmedFromLineEnds()
    {
        var lines = DiffParser.Parse("@@ -1,1 +1,1 @@\r\n+value\r\n");

        var added = Assert.Single(lines, l => l.Kind == DiffLineKind.Added);
        Assert.Equal("value", added.Text);
    }

    [Theory]
    [InlineData("@@ -10,6 +12,7 @@", 10, 12)]
    [InlineData("@@ -1 +1 @@", 1, 1)]
    [InlineData("@@ -0,0 +1,5 @@", 0, 1)]
    [InlineData("@@ -3,4 +3,4 @@ void Method(int a)", 3, 3)]
    public void HunkHeaderStartsAreParsed(string header, int expectedOld, int expectedNew)
    {
        var (oldStart, newStart) = DiffParser.ParseHunkHeader(header);

        Assert.Equal(expectedOld, oldStart);
        Assert.Equal(expectedNew, newStart);
    }

    [Fact]
    public void NewFileNumbersFromLineOne()
    {
        var lines = DiffParser.Parse("@@ -0,0 +1,2 @@\n+alpha\n+beta\n");
        var added = lines.Where(l => l.Kind == DiffLineKind.Added).ToList();

        Assert.Equal(1, added[0].NewLineNumber);
        Assert.Equal(2, added[1].NewLineNumber);
    }
}
