using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using GitGui.Core;

namespace GitGui.App.Controls;

/// <summary>
/// A TextBlock that paints a line of diff from typed runs: syntax colour as the foreground, and a
/// highlight behind the words the diff actually changed.
///
/// It subclasses TextBlock rather than drawing text itself so measurement, wrapping and font
/// fallback all stay Avalonia's problem.
/// </summary>
public class DiffTextBlock : Avalonia.Controls.TextBlock
{
    // Syntax colours. Deliberately muted: the diff's own add/remove colouring has to stay the
    // loudest thing on the row.
    private static readonly IBrush KeywordBrush = new SolidColorBrush(Color.FromRgb(0x88, 0xA4, 0xE8));
    private static readonly IBrush StringBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xA6, 0x6B));
    private static readonly IBrush CommentBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0x8B, 0x6F));
    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.FromRgb(0xB5, 0xA0, 0xD6));

    public static readonly StyledProperty<IReadOnlyList<DiffRun>?> RunsProperty =
        AvaloniaProperty.Register<DiffTextBlock, IReadOnlyList<DiffRun>?>(nameof(Runs));

    /// <summary>Painted behind the words the word-diff marked as changed.</summary>
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<DiffTextBlock, IBrush?>(nameof(HighlightBrush));

    /// <summary>Turns syntax colouring off, leaving the inherited foreground.</summary>
    public static readonly StyledProperty<bool> ShowSyntaxProperty =
        AvaloniaProperty.Register<DiffTextBlock, bool>(nameof(ShowSyntax), defaultValue: true);

    static DiffTextBlock()
    {
        RunsProperty.Changed.AddClassHandler<DiffTextBlock>((c, _) => c.Rebuild());
        HighlightBrushProperty.Changed.AddClassHandler<DiffTextBlock>((c, _) => c.Rebuild());
        ShowSyntaxProperty.Changed.AddClassHandler<DiffTextBlock>((c, _) => c.Rebuild());
    }

    public IReadOnlyList<DiffRun>? Runs
    {
        get => GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public bool ShowSyntax
    {
        get => GetValue(ShowSyntaxProperty);
        set => SetValue(ShowSyntaxProperty, value);
    }

    private void Rebuild()
    {
        Inlines ??= [];
        Inlines.Clear();

        if (Runs is { Count: > 0 } runs)
        {
            foreach (var run in runs)
            {
                var inline = new Run(run.Text);

                if (ShowSyntax && ForegroundFor(run.Token) is { } foreground)
                    inline.Foreground = foreground;

                if (run.IsWordChanged && HighlightBrush is { } highlight)
                    inline.Background = highlight;

                Inlines.Add(inline);
            }
        }

        // Mutating the collection in place does not on its own tell the base class that its
        // cached text layout is stale, so the old colouring would keep being painted.
        InvalidateTextLayout();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static IBrush? ForegroundFor(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => KeywordBrush,
        TokenKind.StringLiteral => StringBrush,
        TokenKind.Comment => CommentBrush,
        TokenKind.Number => NumberBrush,
        _ => null,
    };
}
