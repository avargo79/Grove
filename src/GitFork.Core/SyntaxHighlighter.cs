namespace GitFork.Core;

/// <summary>The kind of thing a run of source text is.</summary>
public enum TokenKind { Plain, Comment, StringLiteral, Keyword, Number, TypeName }

/// <summary>A coloured run within one line of source.</summary>
public sealed record SyntaxToken(string Text, TokenKind Kind);

/// <summary>
/// A deliberately small syntax highlighter: comments, strings, numbers and keywords, per line.
///
/// It is not a parser, and does not try to be. A diff shows fragments of files out of context, so
/// a full grammar would not have the surrounding code it needs anyway. This gets the colouring that
/// makes a diff scannable without adding a TextMate engine and its grammar files as dependencies.
/// </summary>
public static class SyntaxHighlighter
{
    private static readonly string[] CFamilyKeywords =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extends", "extern", "false", "finally", "fixed", "float",
        "for", "foreach", "function", "get", "goto", "if", "implements", "implicit", "import", "in",
        "init", "instanceof", "int", "interface", "internal", "is", "let", "lock", "long",
        "namespace", "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "record", "ref", "required", "return", "sealed", "set",
        "short", "sizeof", "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile",
        "when", "where", "while", "with", "yield", "export", "from", "of", "type",
    ];

    private static readonly string[] ScriptKeywords =
    [
        "and", "as", "assert", "break", "case", "class", "continue", "def", "del", "do", "done",
        "elif", "else", "esac", "except", "fi", "finally", "for", "from", "global", "if", "import",
        "in", "is", "lambda", "local", "nonlocal", "not", "or", "pass", "raise", "return", "then",
        "try", "while", "with", "yield", "echo", "export", "fn", "let", "match", "mut", "pub",
        "self", "True", "False", "None",
    ];

    private sealed record Language(
        string[] Keywords,
        string[] LineComments,
        (string Open, string Close)? BlockComment,
        char[] StringDelimiters,
        bool SupportsEscapes);

    private static readonly Language CFamily = new(
        CFamilyKeywords, ["//"], ("/*", "*/"), ['"', '\''], true);

    private static readonly Language Script = new(
        ScriptKeywords, ["#"], null, ['"', '\''], true);

    private static readonly Language Markup = new(
        [], ["<!--"], ("<!--", "-->"), ['"', '\''], false);

    private static readonly Language Data = new(
        ["true", "false", "null"], [], null, ['"'], true);

    private static readonly Language PlainText = new([], [], null, [], false);

    /// <summary>Picks a language from the file extension. Unknown extensions get no colouring.</summary>
    public static bool IsSupported(string path) => !ReferenceEquals(ForPath(path), PlainText);

    private static Language ForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".java" or ".js" or ".jsx" or ".ts"
            or ".tsx" or ".go" or ".rs" or ".swift" or ".kt" or ".scala" or ".php" or ".css"
            or ".scss" or ".less" => CFamily,
        ".py" or ".rb" or ".sh" or ".bash" or ".zsh" or ".ps1" or ".pl" or ".r" or ".toml"
            or ".ini" or ".conf" or ".yml" or ".yaml" or ".dockerfile" => Script,
        ".xml" or ".html" or ".htm" or ".xaml" or ".axaml" or ".svg" or ".csproj" or ".props"
            or ".targets" or ".plist" => Markup,
        ".json" or ".jsonc" => Data,
        _ => PlainText,
    };

    /// <summary>
    /// Splits one line into coloured runs. Block comments spanning lines are not tracked across
    /// calls, so a diff fragment that starts inside one is simply not marked as a comment — the
    /// alternative is a false positive on every line after an unclosed quote.
    /// </summary>
    public static IReadOnlyList<SyntaxToken> Tokenize(string line, string path)
    {
        var language = ForPath(path);
        if (ReferenceEquals(language, PlainText) || line.Length == 0)
            return [new SyntaxToken(line, TokenKind.Plain)];

        var tokens = new List<SyntaxToken>();
        var buffer = new System.Text.StringBuilder();
        var index = 0;

        void FlushPlain()
        {
            if (buffer.Length == 0)
                return;
            tokens.Add(new SyntaxToken(buffer.ToString(), TokenKind.Plain));
            buffer.Clear();
        }

        while (index < line.Length)
        {
            // A line comment consumes everything after it.
            var comment = language.LineComments.FirstOrDefault(
                marker => string.CompareOrdinal(line, index, marker, 0, marker.Length) == 0);

            if (comment is not null)
            {
                FlushPlain();
                tokens.Add(new SyntaxToken(line[index..], TokenKind.Comment));
                return tokens;
            }

            if (language.BlockComment is { } block &&
                string.CompareOrdinal(line, index, block.Open, 0, block.Open.Length) == 0)
            {
                FlushPlain();
                var close = line.IndexOf(block.Close, index + block.Open.Length, StringComparison.Ordinal);
                var end = close < 0 ? line.Length : close + block.Close.Length;
                tokens.Add(new SyntaxToken(line[index..end], TokenKind.Comment));
                index = end;
                continue;
            }

            var c = line[index];

            if (language.StringDelimiters.Contains(c))
            {
                FlushPlain();
                var end = FindStringEnd(line, index, c, language.SupportsEscapes);
                tokens.Add(new SyntaxToken(line[index..end], TokenKind.StringLiteral));
                index = end;
                continue;
            }

            if (char.IsAsciiDigit(c) && !IsInsideWord(line, index))
            {
                FlushPlain();
                var start = index;
                while (index < line.Length &&
                       (char.IsAsciiLetterOrDigit(line[index]) || line[index] == '.' || line[index] == '_'))
                    index++;
                tokens.Add(new SyntaxToken(line[start..index], TokenKind.Number));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = index;
                while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '_'))
                    index++;

                var word = line[start..index];
                if (language.Keywords.Contains(word, StringComparer.Ordinal))
                {
                    FlushPlain();
                    tokens.Add(new SyntaxToken(word, TokenKind.Keyword));
                }
                else
                {
                    buffer.Append(word);
                }

                continue;
            }

            buffer.Append(c);
            index++;
        }

        FlushPlain();
        return tokens;
    }

    /// <summary>Index just past the closing delimiter, or the end of the line if it never closes.</summary>
    private static int FindStringEnd(string line, int start, char delimiter, bool supportsEscapes)
    {
        var index = start + 1;
        while (index < line.Length)
        {
            if (supportsEscapes && line[index] == '\\' && index + 1 < line.Length)
            {
                index += 2;
                continue;
            }

            if (line[index] == delimiter)
                return index + 1;

            index++;
        }

        return line.Length;
    }

    /// <summary>Digits inside an identifier are part of the identifier, not a number.</summary>
    private static bool IsInsideWord(string line, int index) =>
        index > 0 && (char.IsLetter(line[index - 1]) || line[index - 1] == '_');
}
