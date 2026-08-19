using GitFork.Core;

namespace GitFork.Core.Tests;

public class WordDiffTests
{
    private static string Changed(IReadOnlyList<DiffSegment> segments) =>
        string.Concat(segments.Where(s => s.IsChanged).Select(s => s.Text));

    private static string Rebuilt(IReadOnlyList<DiffSegment> segments) =>
        string.Concat(segments.Select(s => s.Text));

    // ----------------------------------------------------------- tokenizing

    [Fact]
    public void WordsWhitespaceAndPunctuationAreSeparateTokens()
    {
        var tokens = WordDiff.Tokenize("var x = foo(1);");

        Assert.Equal(["var", " ", "x", " ", "=", " ", "foo", "(", "1", ")", ";"], tokens);
    }

    [Fact]
    public void UnderscoresAndDigitsStayInsideAnIdentifier()
    {
        Assert.Equal(["my_var2"], WordDiff.Tokenize("my_var2"));
    }

    [Fact]
    public void TokenizingAnEmptyStringYieldsNothing()
    {
        Assert.Empty(WordDiff.Tokenize(string.Empty));
    }

    // ------------------------------------------------------------ comparing

    [Fact]
    public void IdenticalLinesHaveNothingChanged()
    {
        var (left, right) = WordDiff.Compare("same text", "same text");

        Assert.Empty(Changed(left));
        Assert.Empty(Changed(right));
    }

    [Fact]
    public void OnlyTheChangedWordIsMarked()
    {
        var (left, right) = WordDiff.Compare(
            "the quick brown fox", "the quick red fox");

        Assert.Equal("brown", Changed(left));
        Assert.Equal("red", Changed(right));
    }

    [Fact]
    public void SegmentsAlwaysReconstructTheOriginalLines()
    {
        const string before = "public void Handle(int count, string name)";
        const string after = "public async Task HandleAsync(long count, string name)";

        var (left, right) = WordDiff.Compare(before, after);

        // Anything else would silently corrupt the text on screen.
        Assert.Equal(before, Rebuilt(left));
        Assert.Equal(after, Rebuilt(right));
    }

    [Fact]
    public void AnInsertionMarksOnlyTheInsertedText()
    {
        var (left, right) = WordDiff.Compare("call(a)", "call(a, b)");

        Assert.Empty(Changed(left));
        Assert.Contains("b", Changed(right), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletionMarksOnlyTheRemovedText()
    {
        var (left, right) = WordDiff.Compare("call(a, b)", "call(a)");

        Assert.Contains("b", Changed(left), StringComparison.Ordinal);
        Assert.Empty(Changed(right));
    }

    [Fact]
    public void ChangingIndentationOnlyMarksTheWhitespace()
    {
        var (left, right) = WordDiff.Compare("    return x;", "        return x;");

        Assert.Equal("    ", Changed(left));
        Assert.Equal("        ", Changed(right));
    }

    [Fact]
    public void CompletelyDifferentLinesAreEntirelyChanged()
    {
        var (left, right) = WordDiff.Compare("alpha", "beta");

        Assert.Equal("alpha", Changed(left));
        Assert.Equal("beta", Changed(right));
    }

    [Fact]
    public void AdjacentTokensOfTheSameStateMergeIntoOneSegment()
    {
        var (_, right) = WordDiff.Compare("a", "a very long tail");

        // The trailing run is one segment, not one per token.
        Assert.Single(right.Where(s => s.IsChanged));
    }

    [Fact]
    public void PunctuationChangesDoNotMarkTheWholeExpression()
    {
        var (left, right) = WordDiff.Compare("value[index]", "value(index)");

        Assert.DoesNotContain("value", Changed(left), StringComparison.Ordinal);
        Assert.DoesNotContain("index", Changed(right), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- pairing

    private static DiffHunk Hunk(params (DiffLineKind Kind, string Text)[] lines) => new()
    {
        Header = "@@ -1,1 +1,1 @@",
        OldStart = 1,
        OldCount = 1,
        NewStart = 1,
        NewCount = 1,
        Lines = [.. lines.Select(l => new DiffLine(l.Kind, l.Text, null, null))],
    };

    [Fact]
    public void ARemovalFollowedByAnAdditionIsTreatedAsAReplacement()
    {
        var hunk = Hunk(
            (DiffLineKind.Context, "keep"),
            (DiffLineKind.Removed, "before"),
            (DiffLineKind.Added, "after"));

        var pairs = WordDiff.PairReplacedLines(hunk);

        Assert.Equal(2, Assert.Single(pairs).Value);
        Assert.Equal(1, pairs.Keys.Single());
    }

    [Fact]
    public void SurplusLinesOnEitherSideAreLeftUnpaired()
    {
        var hunk = Hunk(
            (DiffLineKind.Removed, "one"),
            (DiffLineKind.Removed, "two"),
            (DiffLineKind.Added, "uno"));

        var pairs = WordDiff.PairReplacedLines(hunk);

        // Only the first removal has a counterpart to compare against.
        Assert.Single(pairs);
        Assert.Equal(0, pairs.Keys.Single());
    }

    [Fact]
    public void AdditionsWithNoPrecedingRemovalArePairedWithNothing()
    {
        var hunk = Hunk(
            (DiffLineKind.Context, "keep"),
            (DiffLineKind.Added, "brand new"));

        Assert.Empty(WordDiff.PairReplacedLines(hunk));
    }

    [Fact]
    public void SeparateReplacementRunsArePairedIndependently()
    {
        var hunk = Hunk(
            (DiffLineKind.Removed, "a"),
            (DiffLineKind.Added, "A"),
            (DiffLineKind.Context, "middle"),
            (DiffLineKind.Removed, "b"),
            (DiffLineKind.Added, "B"));

        var pairs = WordDiff.PairReplacedLines(hunk);

        Assert.Equal(2, pairs.Count);
        Assert.Equal(1, pairs[0]);
        Assert.Equal(4, pairs[3]);
    }
}
