using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Grove.Core.Graph;

namespace Grove.App.Controls;

/// <summary>
/// Draws the graph cell for a single commit row: the lane lines crossing the row plus this
/// commit's dot. One control per row keeps rendering virtualised with the commit list.
/// </summary>
public class GraphRowControl : Control
{
    public const double LaneWidth = 16;
    private const double DotRadius = 4.5;
    private const double MergeDotRadius = 3.0;
    private const double LineThickness = 2.0;

    /// <summary>Lane colours, cycled by <see cref="GraphRow.ColorIndex"/>.</summary>
    private static readonly Color[] LaneColors =
    [
        Color.FromRgb(0x5A, 0x9B, 0xF6), // blue
        Color.FromRgb(0x63, 0xC3, 0x81), // green
        Color.FromRgb(0xE0, 0x84, 0x4A), // orange
        Color.FromRgb(0xB4, 0x7C, 0xE6), // purple
        Color.FromRgb(0x4F, 0xC1, 0xC0), // teal
        Color.FromRgb(0xE0, 0x5D, 0x7C), // pink
        Color.FromRgb(0xD2, 0xB4, 0x4C), // gold
        Color.FromRgb(0x8A, 0x9B, 0xB0), // slate
    ];

    private static readonly IPen[] LanePens =
        [.. LaneColors.Select(c => (IPen)new Pen(new SolidColorBrush(c), LineThickness, lineCap: PenLineCap.Round))];

    private static readonly IBrush[] LaneBrushes =
        [.. LaneColors.Select(c => (IBrush)new SolidColorBrush(c))];

    public static readonly StyledProperty<GraphRow?> RowProperty =
        AvaloniaProperty.Register<GraphRowControl, GraphRow?>(nameof(Row));

    /// <summary>Painted behind the commit dot so lines do not show through it.</summary>
    public static readonly StyledProperty<IBrush?> DotBackgroundProperty =
        AvaloniaProperty.Register<GraphRowControl, IBrush?>(nameof(DotBackground));

    static GraphRowControl()
    {
        AffectsRender<GraphRowControl>(RowProperty, DotBackgroundProperty);
    }

    public GraphRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public IBrush? DotBackground
    {
        get => GetValue(DotBackgroundProperty);
        set => SetValue(DotBackgroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Row is not { } row)
            return;

        var height = Bounds.Height;
        var middle = height / 2;

        foreach (var edge in row.Edges)
        {
            var pen = LanePens[edge.ColorIndex % LanePens.Length];
            var fromX = LaneCenter(edge.FromLane);
            var toX = LaneCenter(edge.ToLane);

            switch (edge.Kind)
            {
                case GraphEdgeKind.Through:
                    context.DrawLine(pen, new Point(fromX, 0), new Point(fromX, height));
                    break;

                case GraphEdgeKind.Incoming:
                    DrawConnector(context, pen, new Point(fromX, 0), new Point(toX, middle));
                    break;

                case GraphEdgeKind.Outgoing:
                    DrawConnector(context, pen, new Point(fromX, middle), new Point(toX, height));
                    break;
            }
        }

        // Dot last so it sits on top of every line that touches it.
        var center = new Point(LaneCenter(row.Lane), middle);
        var brush = LaneBrushes[row.ColorIndex % LaneBrushes.Length];

        if (row.IsMerge)
        {
            // Hollow ring marks a merge, matching how Fork distinguishes them at a glance.
            context.DrawEllipse(DotBackground, null, center, DotRadius, DotRadius);
            context.DrawEllipse(null, new Pen(brush, 2), center, MergeDotRadius, MergeDotRadius);
        }
        else
        {
            context.DrawEllipse(DotBackground, null, center, DotRadius + 1, DotRadius + 1);
            context.DrawEllipse(brush, null, center, DotRadius, DotRadius);
        }
    }

    /// <summary>Straight when the lane does not change, otherwise an S-curve between the two lanes.</summary>
    private static void DrawConnector(DrawingContext context, IPen pen, Point from, Point to)
    {
        if (Math.Abs(from.X - to.X) < 0.01)
        {
            context.DrawLine(pen, from, to);
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(from, isFilled: false);
            var midY = (from.Y + to.Y) / 2;
            ctx.CubicBezierTo(new Point(from.X, midY), new Point(to.X, midY), to);
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static double LaneCenter(int lane) => (lane * LaneWidth) + (LaneWidth / 2);
}
