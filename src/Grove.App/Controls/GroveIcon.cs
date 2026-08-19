using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Grove.App.Controls;

/// <summary>
/// The application mark: a grove of three trees. Drawn rather than shipped as a bitmap so one
/// definition serves every size the platforms ask for, from a 16px tray glyph to a 1024px macOS
/// icon, with no resampling blur in between.
/// </summary>
/// <remarks>
/// Everything is laid out in a unit square and scaled to fit, so proportions hold at any size.
/// The shapes are deliberately few and large: at 16px a trunk is barely a pixel wide, so the
/// silhouette has to carry the meaning and the canopies are what actually read.
/// </remarks>
public class GroveIcon : Control
{
    private static readonly Color BackgroundTop = Color.FromRgb(0x2C, 0x35, 0x42);
    private static readonly Color BackgroundBottom = Color.FromRgb(0x1A, 0x1F, 0x27);
    private static readonly Color CanopyLight = Color.FromRgb(0x7C, 0xD0, 0x8F);
    private static readonly Color CanopyMid = Color.FromRgb(0x4F, 0xA9, 0x6A);
    private static readonly Color CanopyDark = Color.FromRgb(0x2F, 0x7A, 0x4C);
    private static readonly Color Trunk = Color.FromRgb(0x6B, 0x4F, 0x3A);
    private static readonly Color Ground = Color.FromRgb(0x13, 0x19, 0x1B);

    /// <summary>One tree: a trunk, and a canopy built from overlapping circles.</summary>
    private readonly record struct Tree(
        double X, double TrunkTop, double TrunkWidth, Color Foliage, (double X, double Y, double R)[] Canopy);

    /// <summary>
    /// Back to front. The middle tree is tallest and lightest so the eye lands on it first; the
    /// outer two are darker and shorter, which is what makes three trees read as depth rather
    /// than as a row.
    /// </summary>
    private static readonly Tree[] Trees =
    [
        new(0.212, 0.856, 0.048, CanopyDark,
            [(0.212, 0.600, 0.132), (0.300, 0.652, 0.092)]),
        new(0.788, 0.856, 0.048, CanopyMid,
            [(0.788, 0.582, 0.140), (0.700, 0.648, 0.096)]),
        new(0.500, 0.862, 0.072, CanopyLight,
            [(0.500, 0.372, 0.180), (0.394, 0.474, 0.124), (0.606, 0.474, 0.124)]),
    ];

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0)
            return;

        // Centre the unit square, then draw everything in 0..1 coordinates.
        var origin = new Point((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        using var _ = context.PushTransform(
            Matrix.CreateScale(side, side) * Matrix.CreateTranslation(origin.X, origin.Y));

        DrawBackground(context);

        // Everything after the background is clipped to the same rounded shape, so the ground —
        // which is drawn wider than the icon to keep its curve shallow — cannot square off the
        // bottom corners.
        using var clip = context.PushClip(new RoundedRect(new Rect(0, 0, 1, 1), 0.22));

        DrawGround(context);

        foreach (var tree in Trees)
            DrawTree(context, tree);
    }

    private static void DrawBackground(DrawingContext context)
    {
        var background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(BackgroundTop, 0),
                new GradientStop(BackgroundBottom, 1),
            },
        };

        // The platforms mask the corners themselves, but rounding here keeps the mark looking
        // right anywhere it is drawn raw, such as an in-app about box.
        context.DrawRectangle(background, null, new RoundedRect(new Rect(0, 0, 1, 1), 0.22));
    }

    /// <summary>
    /// A shallow rise the trunks stand on, so they end in ground rather than in mid-air. Wider
    /// than the icon and mostly below it: only the crown of the curve shows.
    /// </summary>
    private static void DrawGround(DrawingContext context)
        => context.DrawEllipse(new SolidColorBrush(Ground), null, new Point(0.5, 1.08), 0.72, 0.22);

    private static void DrawTree(DrawingContext context, Tree tree)
    {
        // The trunk runs from inside the canopy down to the ground, so no seam shows where the
        // two meet however the canopy circles happen to fall.
        var trunk = new SolidColorBrush(Trunk);
        var top = tree.Canopy[0].Y;
        context.DrawRectangle(trunk, null, new RoundedRect(
            new Rect(tree.X - tree.TrunkWidth / 2, top, tree.TrunkWidth, tree.TrunkTop - top),
            tree.TrunkWidth / 2));

        var foliage = new SolidColorBrush(tree.Foliage);
        foreach (var (x, y, r) in tree.Canopy)
            context.DrawEllipse(foliage, null, new Point(x, y), r, r);
    }
}
