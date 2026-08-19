using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using GitFork.App.Controls;
using GitFork.App.ViewModels;
using GitFork.App.Views;
using GitFork.Core;
using GitFork.Core.Tests;

namespace GitFork.App.Tests;

/// <summary>Renders the diff pane and its two layouts, checking colour actually reaches pixels.</summary>
public class DiffRenderTests
{
    /// <summary>A repository whose newest commit changes one word on one line of C#.</summary>
    private static TestRepository CreateOneWordChange()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "Program.cs", "public class App\n{\n    var total = 1;\n}\n");
        fixture.Commit("second", "Program.cs", "public class App\n{\n    var count = 1;\n}\n");
        return fixture;
    }

    private static async Task<(Window Window, MainViewModel ViewModel)> ShowAsync(TestRepository fixture)
    {
        var viewModel = new MainViewModel { WatchForChanges = false };
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        await viewModel.LoadRepositoryAsync(fixture.Path);
        await viewModel.PendingDetailLoad;
        await viewModel.PendingDiffLoad;
        window.UpdateLayout();
        return (window, viewModel);
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    // ------------------------------------------------------- visual tree

    [AvaloniaFact]
    public async Task TheUnifiedListIsShownByDefaultAndTheSideBySideOneIsNot()
    {
        using var fixture = CreateOneWordChange();
        var (window, _) = await ShowAsync(fixture);

        Assert.True(Find<ListBox>(window, "UnifiedList").IsVisible);
        Assert.False(Find<ListBox>(window, "SideBySideList").IsVisible);
    }

    [AvaloniaFact]
    public async Task SwitchingToSideBySideSwapsWhichListIsShown()
    {
        using var fixture = CreateOneWordChange();
        var (window, viewModel) = await ShowAsync(fixture);

        viewModel.Detail!.Diff.Mode = DiffViewMode.SideBySide;
        window.UpdateLayout();

        Assert.False(Find<ListBox>(window, "UnifiedList").IsVisible);
        Assert.True(Find<ListBox>(window, "SideBySideList").IsVisible);
    }

    [AvaloniaFact]
    public async Task TheModeToggleDrivesTheViewModel()
    {
        using var fixture = CreateOneWordChange();
        var (window, viewModel) = await ShowAsync(fixture);

        Find<RadioButton>(window, "SideBySideToggle").IsChecked = true;
        window.UpdateLayout();

        Assert.Equal(DiffViewMode.SideBySide, viewModel.Detail!.Diff.Mode);
    }

    [AvaloniaFact]
    public async Task TheDiffOptionControlsAreBoundToTheViewModel()
    {
        using var fixture = CreateOneWordChange();
        var (window, viewModel) = await ShowAsync(fixture);
        var diff = viewModel.Detail!.Diff;

        Assert.Equal(diff.ContextLines, Find<NumericUpDown>(window, "ContextInput").Value);

        Find<CheckBox>(window, "SyntaxToggle").IsChecked = false;
        window.UpdateLayout();

        Assert.False(diff.ShowSyntaxHighlighting);
    }

    [AvaloniaFact]
    public async Task TheWhitespaceSelectorChangesTheOptionSentToGit()
    {
        using var fixture = CreateOneWordChange();
        var (window, viewModel) = await ShowAsync(fixture);

        Find<ComboBox>(window, "WhitespaceSelector").SelectedIndex = 2;
        window.UpdateLayout();

        Assert.Equal(WhitespaceMode.IgnoreAll, viewModel.Detail!.Diff.Whitespace);
    }

    [AvaloniaFact]
    public async Task DiffLinesAreRenderedThroughTheRunAwareControl()
    {
        using var fixture = CreateOneWordChange();
        var (window, _) = await ShowAsync(fixture);

        var blocks = window.GetVisualDescendants().OfType<DiffTextBlock>().ToList();

        Assert.NotEmpty(blocks);
        Assert.Contains(blocks, b => b.Runs is { Count: > 0 });
    }

    [AvaloniaFact]
    public async Task AChangedWordIsGivenAHighlightBackgroundInline()
    {
        using var fixture = CreateOneWordChange();
        var (window, _) = await ShowAsync(fixture);

        var highlighted = window.GetVisualDescendants().OfType<DiffTextBlock>()
            .SelectMany(b => b.Inlines?.OfType<Run>() ?? [])
            .Where(r => r.Background is not null)
            .ToList();

        Assert.Contains(highlighted, r => r.Text == "total" || r.Text == "count");
    }

    [AvaloniaFact]
    public async Task TurningSyntaxOffStopsTheKeywordColourBeingPainted()
    {
        using var fixture = CreateOneWordChange();
        var (window, viewModel) = await ShowAsync(fixture);

        using (var before = window.CaptureRenderedFrame()!)
            Assert.True(CountNear(before, KeywordColour, KeywordTolerance) > 5, "no keyword colour to begin with");

        viewModel.Detail!.Diff.ShowSyntaxHighlighting = false;
        window.UpdateLayout();

        // The captured frame comes from the composition tree, which needs a render tick to pick
        // up a change that only altered how existing text is painted.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        using var after = window.CaptureRenderedFrame()!;

        // Inline.Foreground is an inherited property and is never null, so only the pixels can
        // say whether the colouring actually went away.
        Assert.Equal(0, CountNear(after, KeywordColour, KeywordTolerance));
        Assert.Contains(
            window.GetVisualDescendants().OfType<DiffTextBlock>()
                .SelectMany(b => b.Inlines?.OfType<Run>() ?? []),
            r => r.Text == "var");
    }

    // ------------------------------------------------------------ pixels

    private static readonly Color KeywordColour = Color.FromRgb(0x88, 0xA4, 0xE8);

    /// <summary>
    /// Tight, because these assertions include "this colour is absent". A loose tolerance also
    /// matches unrelated blues in the theme and would never reach zero.
    /// </summary>
    private const int KeywordTolerance = 10;

    /// <summary>Counts pixels close to a colour, for checking something really was painted.</summary>
    private static int CountNear(WriteableBitmap frame, Color target, int tolerance = 24)
    {
        using var buffer = frame.Lock();
        var count = 0;

        unsafe
        {
            var scan0 = (byte*)buffer.Address;
            for (var y = 0; y < buffer.Size.Height; y++)
            {
                var row = scan0 + (y * buffer.RowBytes);
                for (var x = 0; x < buffer.Size.Width; x++)
                {
                    var p = row + (x * 4);
                    if (Math.Abs(p[0] - target.R) <= tolerance &&
                        Math.Abs(p[1] - target.G) <= tolerance &&
                        Math.Abs(p[2] - target.B) <= tolerance)
                        count++;
                }
            }
        }

        return count;
    }

    [AvaloniaFact]
    public async Task TheWordHighlightIsActuallyPaintedInTheUnifiedView()
    {
        using var fixture = CreateOneWordChange();
        var (window, _) = await ShowAsync(fixture);

        using var frame = window.CaptureRenderedFrame()!;

        // The highlight brush behind changed words; nothing else in the theme is this colour.
        Assert.True(CountNear(frame, Color.FromRgb(0x3F, 0x4A, 0x5A)) > 20,
            "the word-level highlight never reached the screen");
    }

    [AvaloniaFact]
    public async Task SideBySideUsesItsOwnAddedAndRemovedHighlights()
    {
        using var fixture = CreateOneWordChange();
        var (window, viewModel) = await ShowAsync(fixture);

        viewModel.Detail!.Diff.Mode = DiffViewMode.SideBySide;
        window.UpdateLayout();

        using var frame = window.CaptureRenderedFrame()!;

        Assert.True(CountNear(frame, Color.FromRgb(0x2A, 0x6B, 0x36)) > 10, "no added-word highlight");
        Assert.True(CountNear(frame, Color.FromRgb(0x7A, 0x2F, 0x2F)) > 10, "no removed-word highlight");
    }

    [AvaloniaFact]
    public async Task KeywordColourIsActuallyPainted()
    {
        using var fixture = CreateOneWordChange();
        var (window, _) = await ShowAsync(fixture);

        using var frame = window.CaptureRenderedFrame()!;

        // Inlines can carry a brush and still render nothing, as an earlier banner bug showed.
        Assert.True(CountNear(frame, KeywordColour, KeywordTolerance) > 5,
            "keyword colouring never reached the screen");
    }

    [AvaloniaFact]
    public async Task TheEmptyStateIsShownWhenThereIsNothingToDiff()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("reindent only", "a.txt", "  one\n");

        var (window, viewModel) = await ShowAsync(fixture);

        viewModel.Detail!.Diff.Whitespace = WhitespaceMode.IgnoreAll;
        await viewModel.Detail.PendingDiffLoad;
        window.UpdateLayout();

        var label = Find<TextBlock>(window, "EmptyLabel");
        Assert.True(label.IsVisible);
        Assert.Equal("No changes once whitespace is ignored.", label.Text);
        Assert.True(label.Bounds.Width > 0, "the empty-state label measured to zero width");
    }

    [AvaloniaFact]
    public async Task TheWholeDiffPaneRendersWithoutThrowingInBothModes()
    {
        using var fixture = CreateOneWordChange();
        var (window, viewModel) = await ShowAsync(fixture);

        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));

        viewModel.Detail!.Diff.Mode = DiffViewMode.SideBySide;
        window.UpdateLayout();

        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));
    }
}
