using Grove.Core;

namespace Grove.Core.Tests;

public class SyntaxHighlighterTests
{
    private static string TextOf(IReadOnlyList<SyntaxToken> tokens, TokenKind kind) =>
        string.Concat(tokens.Where(t => t.Kind == kind).Select(t => t.Text));

    private static string Rebuilt(IReadOnlyList<SyntaxToken> tokens) =>
        string.Concat(tokens.Select(t => t.Text));

    [Theory]
    [InlineData("Program.cs", true)]
    [InlineData("app.ts", true)]
    [InlineData("script.py", true)]
    [InlineData("page.axaml", true)]
    [InlineData("data.json", true)]
    [InlineData("notes.txt", false)]
    [InlineData("LICENSE", false)]
    public void LanguagesAreRecognisedByExtension(string path, bool supported)
    {
        Assert.Equal(supported, SyntaxHighlighter.IsSupported(path));
    }

    [Fact]
    public void AnUnknownFileTypeIsLeftEntirelyPlain()
    {
        var tokens = SyntaxHighlighter.Tokenize("public class Foo // comment", "notes.txt");

        Assert.Equal(TokenKind.Plain, Assert.Single(tokens).Kind);
    }

    [Fact]
    public void TokensAlwaysReconstructTheOriginalLine()
    {
        const string line = "    public async Task<int> Run(string name) // does the thing";

        // Anything else would silently corrupt the code shown on screen.
        Assert.Equal(line, Rebuilt(SyntaxHighlighter.Tokenize(line, "Program.cs")));
    }

    [Fact]
    public void KeywordsAreMarkedInCFamilyCode()
    {
        var tokens = SyntaxHighlighter.Tokenize("public static void Main()", "Program.cs");

        Assert.Equal("publicstaticvoid", TextOf(tokens, TokenKind.Keyword));
    }

    [Fact]
    public void IdentifiersThatMerelyContainAKeywordAreNotMarked()
    {
        var tokens = SyntaxHighlighter.Tokenize("classification = intValue;", "Program.cs");

        Assert.Empty(TextOf(tokens, TokenKind.Keyword));
    }

    [Fact]
    public void ALineCommentSwallowsTheRestOfTheLine()
    {
        var tokens = SyntaxHighlighter.Tokenize("var x = 1; // set x to 1", "Program.cs");

        Assert.Equal("// set x to 1", TextOf(tokens, TokenKind.Comment));
    }

    [Fact]
    public void AStringIsOneTokenIncludingItsQuotes()
    {
        var tokens = SyntaxHighlighter.Tokenize("""var s = "hello world";""", "Program.cs");

        Assert.Equal(""""
            "hello world"
            """", TextOf(tokens, TokenKind.StringLiteral).Trim());
    }

    [Fact]
    public void AnEscapedQuoteDoesNotEndTheString()
    {
        var tokens = SyntaxHighlighter.Tokenize("""var s = "a \" b"; var t = 1;""", "Program.cs");

        Assert.Contains("\\\"", TextOf(tokens, TokenKind.StringLiteral), StringComparison.Ordinal);
        Assert.Contains("1", TextOf(tokens, TokenKind.Number), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnterminatedStringRunsToTheEndOfTheLineRatherThanThrowing()
    {
        var tokens = SyntaxHighlighter.Tokenize("""var s = "never closed""", "Program.cs");

        Assert.Contains("never closed", TextOf(tokens, TokenKind.StringLiteral), StringComparison.Ordinal);
    }

    [Fact]
    public void CommentMarkersInsideAStringAreNotAComment()
    {
        var tokens = SyntaxHighlighter.Tokenize("""var url = "https://example.com";""", "Program.cs");

        Assert.Empty(TextOf(tokens, TokenKind.Comment));
    }

    [Fact]
    public void NumbersAreMarkedButNotDigitsInsideIdentifiers()
    {
        var tokens = SyntaxHighlighter.Tokenize("var total2 = 42 + 3.5;", "Program.cs");

        Assert.Equal("423.5", TextOf(tokens, TokenKind.Number));
    }

    [Fact]
    public void ABlockCommentThatClosesOnTheSameLineIsBounded()
    {
        var tokens = SyntaxHighlighter.Tokenize("var x = /* inline */ 1;", "Program.cs");

        Assert.Equal("/* inline */", TextOf(tokens, TokenKind.Comment));
        Assert.Equal("1", TextOf(tokens, TokenKind.Number));
    }

    [Fact]
    public void AnUnclosedBlockCommentRunsToTheEndOfTheLine()
    {
        var tokens = SyntaxHighlighter.Tokenize("var x = 1; /* opened here", "Program.cs");

        Assert.Equal("/* opened here", TextOf(tokens, TokenKind.Comment));
    }

    [Fact]
    public void ShellAndPythonUseHashComments()
    {
        var tokens = SyntaxHighlighter.Tokenize("value = 1  # explain", "script.py");

        Assert.Equal("# explain", TextOf(tokens, TokenKind.Comment));
        Assert.Equal("1", TextOf(tokens, TokenKind.Number));
    }

    [Fact]
    public void PythonKeywordsAreMarked()
    {
        var tokens = SyntaxHighlighter.Tokenize("def run(self):", "script.py");

        Assert.Contains("def", TextOf(tokens, TokenKind.Keyword), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLiteralsAreMarkedAsKeywords()
    {
        var tokens = SyntaxHighlighter.Tokenize("""{"enabled": true, "count": 3}""", "config.json");

        Assert.Equal("true", TextOf(tokens, TokenKind.Keyword));
        Assert.Equal("3", TextOf(tokens, TokenKind.Number));
    }

    [Fact]
    public void MarkupAttributesAreMarkedAsStrings()
    {
        var tokens = SyntaxHighlighter.Tokenize("""<Button Content="Save" />""", "View.axaml");

        Assert.Equal(""""
            "Save"
            """", TextOf(tokens, TokenKind.StringLiteral).Trim());
    }

    [Fact]
    public void AnXmlCommentIsMarked()
    {
        var tokens = SyntaxHighlighter.Tokenize("<!-- a note -->", "View.axaml");

        Assert.Equal("<!-- a note -->", TextOf(tokens, TokenKind.Comment));
    }

    [Fact]
    public void AnEmptyLineProducesASingleEmptyToken()
    {
        var tokens = SyntaxHighlighter.Tokenize(string.Empty, "Program.cs");

        Assert.Equal(string.Empty, Assert.Single(tokens).Text);
    }

    [Fact]
    public void AdjacentPlainTextIsNotSplitIntoManyTokens()
    {
        var tokens = SyntaxHighlighter.Tokenize("Foo.Bar.Baz(qux)", "Program.cs");

        // No keywords, strings or numbers here, so it should be one plain run.
        Assert.Single(tokens);
    }
}
