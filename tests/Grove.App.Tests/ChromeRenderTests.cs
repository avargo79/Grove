using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Grove.Core.Tests;
using Xunit;

namespace Grove.App.Tests;

/// <summary>
/// The look of the chrome, asserted on pixels. Styles are the easiest part of this app to break
/// silently: a selector that stops matching leaves the control in the visual tree, correctly
/// bound and completely wrong-looking, which no tree assertion notices.
/// </summary>
public class ChromeRenderTests
{
    /// <summary>The accent, allowing for the antialiasing at the button's rounded edges.</summary>
    private static bool IsAccentBlue(Pixel p) => p.B > 200 && p.B - p.R > 100 && p.G > p.R;

    private static List<Pixel> ReadPixels(WriteableBitmap bitmap, PixelRect region) =>
        FramePixels.Read(bitmap, region);

    private static PixelRect BoundsOf(Visual control, Visual root) => FramePixels.BoundsOf(control, root);

    private static Pixel Average(List<Pixel> pixels) => pixels.Average();

    private static int Distance(Pixel a, Pixel b) => a.DistanceTo(b);

    [AvaloniaFact]
    public async Task TheSelectedCommitRowIsTintedRatherThanFlooded()
    {
        using var fixture = TestRepository.CreateEmpty();
        for (var i = 0; i < 6; i++)
            fixture.Commit($"commit {i}", "file.txt", $"line {i}\n");

        var (window, _, viewModel) = await TestShell.OpenAsync(fixture.Path);
        viewModel.SelectedCommit = viewModel.Commits[0];
        window.UpdateLayout();

        var rows = window.GetVisualDescendants().OfType<ListBoxItem>().Take(2).ToList();
        using var frame = window.CaptureRenderedFrame()!;

        var selected = Average(ReadPixels(frame, BoundsOf(rows[0], window)));
        var unselected = Average(ReadPixels(frame, BoundsOf(rows[1], window)));

        // Far enough from its neighbours to find at a glance…
        Assert.True(Distance(selected, unselected) > 15,
            $"selected {selected} is barely distinct from {unselected}");

        // …but still a tint: a saturated bar across a dense list drowns out the badges and lane
        // colours that share the row.
        Assert.True(selected.Saturation < 70,
            $"selected row is a flood fill, not a tint (saturation {selected.Saturation})");
    }

    [AvaloniaFact]
    public async Task TheCommitButtonIsTheOneFilledControlInTheStagingPane()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "tracked.txt", "one\n");
        fixture.WriteFile("staged.txt", "staged\n");
        fixture.Git("add", "staged.txt");

        var (window, _, viewModel) = await TestShell.OpenAsync(fixture.Path);
        viewModel.SelectWorkingCopy();

        // The button only fills once it would actually do something: staged files and a message.
        viewModel.WorkingCopy!.CommitMessage = "a message";
        window.UpdateLayout();

        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "CommitButton");
        Assert.True(button.IsEffectivelyEnabled, "the commit button needs to be live to be filled");

        using var frame = window.CaptureRenderedFrame()!;
        var filled = ReadPixels(frame, BoundsOf(button, window)).Count(IsAccentBlue);

        Assert.True(filled > 200, $"the commit button is not filled with the accent ({filled} px)");
    }

    [AvaloniaFact]
    public async Task TheSidebarIsSeparatedFromTheHistoryByAHairline()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var (window, _, _) = await TestShell.OpenAsync(fixture.Path);
        window.UpdateLayout();

        var sidebar = window.GetVisualDescendants().OfType<ListBox>()
            .First(l => l.Classes.Contains("flat"));
        var edge = BoundsOf(sidebar, window);

        using var frame = window.CaptureRenderedFrame()!;

        // A column either side of the boundary: the divider is one pixel of border colour, not a
        // painted bar, so the two panels have to differ without a wide seam between them.
        var left = Average(ReadPixels(frame, new PixelRect(edge.X + 10, edge.Y + 40, 20, 30)));
        var right = Average(ReadPixels(frame, new PixelRect(edge.Right + 60, edge.Y + 40, 20, 30)));

        Assert.True(Distance(left, right) > 4, $"sidebar {left} and history {right} are the same surface");
    }
}
