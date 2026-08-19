namespace Grove.Core;

/// <summary>How much whitespace difference to treat as no difference at all.</summary>
public enum WhitespaceMode
{
    /// <summary>Every difference counts.</summary>
    Show,

    /// <summary>Ignore changes in the amount of existing whitespace.</summary>
    IgnoreChange,

    /// <summary>Ignore all whitespace, including indentation added or removed.</summary>
    IgnoreAll,

    /// <summary>Ignore lines that are entirely blank.</summary>
    IgnoreBlankLines,
}

/// <summary>
/// Presentation options for a diff. Threaded through every diff call so the view can widen the
/// context or hide reindentation noise without the caller reassembling argument lists.
/// </summary>
public sealed record DiffOptions
{
    public static DiffOptions Default { get; } = new();

    /// <summary>Unchanged lines shown either side of a change.</summary>
    public int ContextLines { get; init; } = 3;

    public WhitespaceMode Whitespace { get; init; } = WhitespaceMode.Show;

    /// <summary>Detect renames so a moved file reads as a move rather than a delete plus an add.</summary>
    public bool DetectRenames { get; init; } = true;

    /// <summary>The git flags these options correspond to.</summary>
    public IEnumerable<string> ToArguments()
    {
        yield return $"--unified={Math.Max(0, ContextLines)}";

        if (DetectRenames)
            yield return "-M";

        switch (Whitespace)
        {
            case WhitespaceMode.IgnoreChange:
                yield return "--ignore-space-change";
                break;
            case WhitespaceMode.IgnoreAll:
                yield return "--ignore-all-space";
                break;
            case WhitespaceMode.IgnoreBlankLines:
                yield return "--ignore-blank-lines";
                break;
            case WhitespaceMode.Show:
            default:
                break;
        }
    }
}
