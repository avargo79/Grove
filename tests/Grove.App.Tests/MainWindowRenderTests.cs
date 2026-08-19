using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Grove.App.Controls;
using Grove.App.ViewModels;
using Grove.App.Views;
using Grove.Core.Tests;

namespace Grove.App.Tests;

/// <summary>
/// Renders the real window through Skia on Avalonia's headless platform and inspects the resulting
/// pixels. These are the tests a screenshot would otherwise stand in for: they prove the XAML binds,
/// the templates apply, and the custom graph control actually paints coloured lanes.
/// </summary>
public class MainWindowRenderTests
{
    /// <summary>main and feature diverge and are merged back, so the graph needs two lanes.</summary>
    private static TestRepository CreateBranchedRepository()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base commit", "README.md", "hello\nworld\n");
        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("add feature", "feature.txt", "feature\n");
        fixture.Git("checkout", "--quiet", "main");
        fixture.Commit("update readme", "README.md", "hello\nchanged\n");
        fixture.Git("merge", "--quiet", "--no-ff", "feature", "-m", "merge feature");
        return fixture;
    }

    private static async Task<(Window Window, MainViewModel ViewModel)> ShowAsync(TestRepository fixture)
    {
        var (window, _, viewModel) = await TestShell.OpenAsync(fixture.Path);
        return (window, viewModel);
    }


    private static IEnumerable<string> VisibleText(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))!;

    // ---------------------------------------------------------- visual tree

    [AvaloniaFact]
    public void TheWindowOpensWithNoGraphRowsBeforeARepositoryIsLoaded()
    {
        var (window, _) = TestShell.Empty();

        Assert.Empty(window.GetVisualDescendants().OfType<GraphRowControl>());
        Assert.Contains("Open a repository to get started.", VisibleText(window));
    }

    [AvaloniaFact]
    public async Task TheCommitListRealisesARowPerCommit()
    {
        using var fixture = CreateBranchedRepository();
        var (window, viewModel) = await ShowAsync(fixture);

        var list = window.GetVisualDescendants().OfType<ListBox>()
            .First(l => l.Name == "CommitList");

        Assert.Equal(4, viewModel.Commits.Count);
        Assert.Equal(viewModel.Commits.Count, list.GetRealizedContainers().Count());
    }

    [AvaloniaFact]
    public async Task EveryRealisedRowCarriesAGraphControlWithLayoutAndWidth()
    {
        using var fixture = CreateBranchedRepository();
        var (window, viewModel) = await ShowAsync(fixture);

        var graphs = window.GetVisualDescendants().OfType<GraphRowControl>().ToList();

        Assert.Equal(viewModel.Commits.Count, graphs.Count);
        Assert.All(graphs, g => Assert.NotNull(g.Row));
        // A zero-sized graph column would silently render nothing at all.
        Assert.All(graphs, g => Assert.True(g.Bounds is { Width: > 0, Height: > 0 }));
    }

    [AvaloniaFact]
    public async Task TheSidebarRendersItsSectionsAndBranchNames()
    {
        using var fixture = CreateBranchedRepository();
        var (window, _) = await ShowAsync(fixture);
        var texts = VisibleText(window).ToList();

        Assert.Contains("Branches", texts);
        Assert.Contains("main", texts);
        Assert.Contains("feature", texts);
    }

    [AvaloniaFact]
    public async Task RefBadgesAreRenderedWithTheirStyleClasses()
    {
        using var fixture = CreateBranchedRepository();
        var (window, _) = await ShowAsync(fixture);

        var badges = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("badge")).ToList();

        Assert.NotEmpty(badges);
        Assert.Contains(badges, b => b.Classes.Contains("head"));
    }

    [AvaloniaFact]
    public async Task TheDetailPaneShowsTheSelectedCommitSubjectAndFileList()
    {
        using var fixture = CreateBranchedRepository();
        var (window, viewModel) = await ShowAsync(fixture);

        viewModel.SelectedCommit = viewModel.Commits.Single(c => c.Subject == "update readme");
        await viewModel.PendingDetailLoad;
        await viewModel.PendingDiffLoad;
        window.UpdateLayout();

        var texts = VisibleText(window).ToList();
        Assert.Contains("update readme", texts);
        Assert.Contains("README.md", texts);
        Assert.Contains("1 file changed", texts);
    }

    [AvaloniaFact]
    public async Task TheDiffPaneRendersTheAddedAndRemovedLines()
    {
        using var fixture = CreateBranchedRepository();
        var (window, viewModel) = await ShowAsync(fixture);

        viewModel.SelectedCommit = viewModel.Commits.Single(c => c.Subject == "update readme");
        await viewModel.PendingDetailLoad;
        await viewModel.PendingDiffLoad;
        window.UpdateLayout();

        var texts = VisibleText(window).ToList();
        Assert.Contains("changed", texts);
        Assert.Contains("world", texts);
    }

    [AvaloniaFact]
    public async Task ALongCommitMessageDoesNotPushTheFileListOutOfView()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1\n");
        fixture.WriteFile("a.txt", "2\n");
        fixture.WriteFile("b.txt", "new\n");
        fixture.Git("add", "-A");
        // A wall-of-text body of the kind a real squash-merge produces.
        var body = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i} of the commit message."));
        fixture.Git("commit", "--quiet", "-m", "big message", "-m", body);

        var (window, viewModel) = await ShowAsync(fixture);

        Assert.Equal(2, viewModel.Detail!.Files.Count);

        var fileList = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "FileList");
        Assert.Equal(2, fileList.GetRealizedContainers().Count());

        // The list must sit inside the window, not below its bottom edge.
        var bounds = fileList.Bounds;
        var origin = fileList.TranslatePoint(new Point(0, 0), window)!.Value;
        Assert.True(bounds.Height > 0, "file list collapsed to zero height");
        Assert.True(origin.Y + bounds.Height <= window.Bounds.Height + 1,
            $"file list overflows the window: bottom {origin.Y + bounds.Height} vs {window.Bounds.Height}");
    }

    // -------------------------------------------------------------- pixels

    [AvaloniaFact]
    public async Task TheRenderedFrameIsPaintedWithTheDarkThemeBackground()
    {
        using var fixture = CreateBranchedRepository();
        var (window, _) = await ShowAsync(fixture);

        using var frame = window.CaptureRenderedFrame()!;
        var pixels = ReadPixels(frame);

        Assert.NotEmpty(pixels);
        // A transparent or white frame would mean the theme never applied.
        Assert.All(pixels, p => Assert.Equal(255, p.A));
        Assert.True(AverageBrightness(pixels) < 100, "expected a dark surface");
    }

    [AvaloniaFact]
    public async Task TheGraphColumnIsPaintedWithLaneColoursFromThePalette()
    {
        using var fixture = CreateBranchedRepository();
        var (window, _) = await ShowAsync(fixture);

        using var frame = window.CaptureRenderedFrame()!;
        var pixels = ReadPixels(frame);

        // The lane palette is saturated; every surface and text colour in the theme is not.
        // Finding saturated pixels proves the graph control actually drew lanes and dots.
        var saturated = pixels.Count(IsSaturated);
        Assert.True(saturated > 50, $"expected painted lane pixels, found {saturated}");
    }

    [AvaloniaFact]
    public async Task ARepositoryWithNoBranchesStillRendersASingleLane()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1\n");
        fixture.Commit("second", "a.txt", "2\n");

        var (window, viewModel) = await ShowAsync(fixture);
        using var frame = window.CaptureRenderedFrame()!;

        Assert.All(viewModel.Commits, c => Assert.Equal(1, c.GraphRow.LaneCount));
        Assert.True(ReadPixels(frame).Count(IsSaturated) > 20);
    }

    [AvaloniaFact]
    public async Task RenderingTheWholeWindowNeverThrows()
    {
        using var fixture = CreateBranchedRepository();
        var (window, _) = await ShowAsync(fixture);

        // Exercises every template, style and custom Render implementation in one pass.
        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));
    }

    // ------------------------------------------------------------- helpers

    private readonly record struct Pixel(byte B, byte G, byte R, byte A);

    /// <summary>Copies the captured frame into BGRA pixels for inspection.</summary>
    private static List<Pixel> ReadPixels(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var width = buffer.Size.Width;
        var height = buffer.Size.Height;
        var pixels = new List<Pixel>(width * height);

        unsafe
        {
            var scan0 = (byte*)buffer.Address;
            for (var y = 0; y < height; y++)
            {
                var row = scan0 + (y * buffer.RowBytes);
                for (var x = 0; x < width; x++)
                {
                    var p = row + (x * 4);
                    pixels.Add(new Pixel(p[0], p[1], p[2], p[3]));
                }
            }
        }

        return pixels;
    }

    private static double AverageBrightness(List<Pixel> pixels) =>
        pixels.Average(p => (p.R + p.G + p.B) / 3.0);

    /// <summary>True for colours far more vivid than any theme surface or text colour.</summary>
    private static bool IsSaturated(Pixel p)
    {
        var max = Math.Max(p.R, Math.Max(p.G, p.B));
        var min = Math.Min(p.R, Math.Min(p.G, p.B));
        return max > 90 && max - min > 60;
    }
}
