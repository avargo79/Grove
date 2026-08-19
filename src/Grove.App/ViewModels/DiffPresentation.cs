using Grove.Core;

namespace Grove.App.ViewModels;

/// <summary>
/// How diffs are presented, independent of which diff is on screen. A detail pane is rebuilt
/// every time another commit is selected, so the choices a user makes in one have to live
/// somewhere longer-lived than the pane itself.
/// </summary>
public readonly record struct DiffPresentation(
    int ContextLines,
    WhitespaceMode Whitespace,
    bool ShowSyntaxHighlighting,
    bool ShowWordHighlighting)
{
    public static DiffPresentation Default => From(new AppSettings());

    public static DiffPresentation From(AppSettings settings) => new(
        settings.DiffContextLines,
        settings.DiffWhitespace,
        settings.ShowSyntaxHighlighting,
        settings.ShowWordHighlighting);

    public static DiffPresentation From(DiffViewModel diff) => new(
        diff.ContextLines,
        diff.Whitespace,
        diff.ShowSyntaxHighlighting,
        diff.ShowWordHighlighting);

    public void ApplyTo(DiffViewModel diff)
    {
        diff.ContextLines = ContextLines;
        diff.Whitespace = Whitespace;
        diff.ShowSyntaxHighlighting = ShowSyntaxHighlighting;
        diff.ShowWordHighlighting = ShowWordHighlighting;
    }
}
