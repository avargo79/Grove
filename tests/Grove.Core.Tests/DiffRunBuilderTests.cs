using Grove.Core;

namespace Grove.Core.Tests;

public class DiffRunBuilderTests
{
    private static string Rebuilt(IReadOnlyList<DiffRun> runs) =>
        string.Concat(runs.Select(r => r.Text));

    private static string ChangedText(IReadOnlyList<DiffRun> runs) =>
        string.Concat(runs.Where(r => r.IsWordChanged).Select(r => r.Text));

    [Fact]
    public void RunsAlwaysReconstructTheLine()
    {
        const string line = "public int Count = 42; // total";
        var (_, segments) = WordDiff.Compare("public int Count = 41; // total", line);

        Assert.Equal(line, Rebuilt(DiffRunBuilder.Build(line, segments, "Program.cs")));
    }

    [Fact]
    public void SyntaxColourAndWordHighlightAreBothPreserved()
    {
        var (_, segments) = WordDiff.Compare("var x = 1;", "var y = 1;");
        var runs = DiffRunBuilder.Build("var y = 1;", segments, "Program.cs");

        // The keyword keeps its colour even though a different word changed.
        Assert.Contains(runs, r => r is { Text: "var", Token: TokenKind.Keyword, IsWordChanged: false });
        Assert.Equal("y", ChangedText(runs));
    }

    [Fact]
    public void ARunIsSplitWhereTheChangedStateFlipsInsideAToken()
    {
        // "counter" is one syntax token, but only part of the line changed around it.
        var (_, segments) = WordDiff.Compare("total = 1;", "counter = 1;");
        var runs = DiffRunBuilder.Build("counter = 1;", segments, "Program.cs");

        Assert.Equal("counter", ChangedText(runs));
        Assert.Equal("counter = 1;", Rebuilt(runs));
    }

    [Fact]
    public void AChangedStringLiteralKeepsItsStringColour()
    {
        var (_, segments) = WordDiff.Compare("""var s = "before";""", """var s = "after";""");
        var runs = DiffRunBuilder.Build("""var s = "after";""", segments, "Program.cs");

        Assert.Contains(runs, r => r.Token == TokenKind.StringLiteral && r.IsWordChanged);
    }

    [Fact]
    public void WithNoSegmentsNothingIsMarkedAsChanged()
    {
        var runs = DiffRunBuilder.Build("public void Run()", null, "Program.cs");

        Assert.Empty(ChangedText(runs));
        Assert.Contains(runs, r => r.Token == TokenKind.Keyword);
    }

    [Fact]
    public void AnEmptyLineProducesNoRuns()
    {
        Assert.Empty(DiffRunBuilder.Build(string.Empty, null, "Program.cs"));
        Assert.Empty(DiffRunBuilder.BuildPlain(string.Empty, "Program.cs"));
    }

    [Fact]
    public void PlainRunsCarrySyntaxColourWithoutHighlighting()
    {
        var runs = DiffRunBuilder.BuildPlain("return null; // done", "Program.cs");

        Assert.Empty(ChangedText(runs));
        Assert.Contains(runs, r => r.Token == TokenKind.Keyword);
        Assert.Contains(runs, r => r.Token == TokenKind.Comment);
    }

    [Fact]
    public void PlainRunsCanMarkTheWholeLineAsChanged()
    {
        var runs = DiffRunBuilder.BuildPlain("return null;", "Program.cs", allChanged: true);

        Assert.Equal("return null;", ChangedText(runs));
    }

    [Fact]
    public void AnUnknownFileTypeStillProducesUsableRuns()
    {
        var (_, segments) = WordDiff.Compare("alpha beta", "alpha gamma");
        var runs = DiffRunBuilder.Build("alpha gamma", segments, "notes.txt");

        Assert.Equal("alpha gamma", Rebuilt(runs));
        Assert.Equal("gamma", ChangedText(runs));
        Assert.All(runs, r => Assert.Equal(TokenKind.Plain, r.Token));
    }

    [Fact]
    public void SegmentsShorterThanTheLineDoNotRunOffTheEnd()
    {
        // A defensive case: mismatched segments must not throw or drop text.
        var segments = new List<DiffSegment> { new("abc", true) };
        var runs = DiffRunBuilder.Build("abcdef", segments, "notes.txt");

        Assert.Equal("abcdef", Rebuilt(runs));
        Assert.Equal("abc", ChangedText(runs));
    }
}
