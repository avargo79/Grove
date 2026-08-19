using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Grove.App.ViewModels;
using Grove.App.Views;
using Grove.Core.Tests;

namespace Grove.App.Tests;

/// <summary>Renders the M3 chrome — network toolbar and conflict banner — and checks it behaves.</summary>
public class OperationsRenderTests
{
    private static async Task<(Window Window, MainViewModel ViewModel)> ShowAsync(TestRepository fixture)
    {
        var (window, _, viewModel) = await TestShell.OpenAsync(fixture.Path);
        return (window, viewModel);
    }

    private static TestRepository CreateConflictingBranches()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "shared.txt", "original\n");
        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("feature edit", "shared.txt", "from feature\n");
        fixture.Git("checkout", "--quiet", "main");
        fixture.Commit("main edit", "shared.txt", "from main\n");
        return fixture;
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public async Task TheNetworkToolbarAppearsOnceARepositoryIsOpen()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        var (window, _) = await ShowAsync(fixture);

        Assert.True(Find<Button>(window, "FetchButton").IsEffectivelyVisible);
        Assert.True(Find<SplitButton>(window, "PullButton").IsEffectivelyVisible);
        Assert.True(Find<SplitButton>(window, "PushButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ThereIsNoRepositoryUiUntilOneIsOpen()
    {
        var (window, _) = TestShell.Empty();

        // The shell only builds a repository view once there is a repository to build it for.
        Assert.Empty(window.GetVisualDescendants().OfType<RepositoryView>());
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "FetchButton");
        Assert.True(Find<StackPanel>(window, "EmptyState").IsVisible);
    }

    [AvaloniaFact]
    public async Task TheConflictBannerIsHiddenWhenNothingIsInProgress()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        var (window, _) = await ShowAsync(fixture);

        Assert.False(Find<Border>(window, "ConflictBanner").IsVisible);
    }

    [AvaloniaFact]
    public async Task AConflictingMergeRaisesTheBannerWithItsActions()
    {
        using var fixture = CreateConflictingBranches();
        var (window, viewModel) = await ShowAsync(fixture);
        viewModel.Commands!.ConfirmAsync = _ => Task.FromResult(true);

        var feature = viewModel.Sections.Single(s => s.Title == "Branches")
            .Items.Single(i => i.DisplayName == "feature");

        await viewModel.Commands.MergeRefCommand.ExecuteAsync(feature);
        await viewModel.Commands.PendingOperation;
        window.UpdateLayout();

        var banner = Find<Border>(window, "ConflictBanner");
        Assert.True(banner.IsVisible);
        Assert.True(Find<Button>(window, "ContinueButton").IsVisible);
        Assert.True(Find<Button>(window, "AbortButton").IsVisible);

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t is not null && t.Contains("Merge in progress", StringComparison.Ordinal));
        Assert.Contains(texts, t => t is not null && t.Contains("1 file still has conflicts.", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task TheBannerLabelIsActuallyLegibleAndNotDarkOnDark()
    {
        using var fixture = CreateConflictingBranches();
        var (window, viewModel) = await ShowAsync(fixture);
        viewModel.Commands!.ConfirmAsync = _ => Task.FromResult(true);

        var feature = viewModel.Sections.Single(s => s.Title == "Branches")
            .Items.Single(i => i.DisplayName == "feature");

        await viewModel.Commands.MergeRefCommand.ExecuteAsync(feature);
        await viewModel.Commands.PendingOperation;
        window.UpdateLayout();

        var banner = Find<Border>(window, "ConflictBanner");
        var origin = banner.TranslatePoint(new Point(0, 0), window)!.Value;

        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();

        // The label was once present in the tree but inherited a dark foreground, so it rendered
        // invisibly against the banner. Only the pixels can tell the difference.
        var bright = 0;
        unsafe
        {
            var scan0 = (byte*)buffer.Address;
            var top = Math.Max(0, (int)origin.Y);
            var bottom = Math.Min(buffer.Size.Height, (int)(origin.Y + banner.Bounds.Height));

            for (var y = top; y < bottom; y++)
            {
                var row = scan0 + (y * buffer.RowBytes);
                for (var x = 0; x < Math.Min(240, buffer.Size.Width); x++)
                {
                    var p = row + (x * 4);
                    if (p[0] > 190 && p[1] > 190 && p[2] > 190)
                        bright++;
                }
            }
        }

        var label = Find<TextBlock>(window, "BannerLabel");
        Assert.True(label.Bounds.Width > 0, $"the banner label measured to zero width: '{label.Text}'");
        Assert.True(bright > 30, $"the banner label is not legible: only {bright} light pixels");
    }

    [AvaloniaFact]
    public async Task AbortingFromTheBannerHidesItAgain()
    {
        using var fixture = CreateConflictingBranches();
        var (window, viewModel) = await ShowAsync(fixture);
        viewModel.Commands!.ConfirmAsync = _ => Task.FromResult(true);

        var feature = viewModel.Sections.Single(s => s.Title == "Branches")
            .Items.Single(i => i.DisplayName == "feature");

        await viewModel.Commands.MergeRefCommand.ExecuteAsync(feature);
        await viewModel.Commands.PendingOperation;

        await viewModel.Commands.AbortOperationCommand.ExecuteAsync(null);
        await viewModel.Commands.PendingOperation;
        window.UpdateLayout();

        Assert.False(Find<Border>(window, "ConflictBanner").IsVisible);
    }

    [AvaloniaFact]
    public async Task TheStatusBarShowsTheLastOperationResult()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "feature");

        var (window, viewModel) = await ShowAsync(fixture);
        var feature = viewModel.Sections.Single(s => s.Title == "Branches")
            .Items.Single(i => i.DisplayName == "feature");

        await viewModel.Commands!.CheckoutRefCommand.ExecuteAsync(feature);
        await viewModel.Commands.PendingOperation;
        window.UpdateLayout();

        var status = Find<TextBlock>(window, "OperationStatus");
        Assert.Equal("Checked out 'feature'.", status.Text);
        Assert.DoesNotContain("error", status.Classes);
    }

    [AvaloniaFact]
    public async Task AFailedOperationIsStyledAsAnError()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var (window, viewModel) = await ShowAsync(fixture);

        // Nowhere to push to.
        await viewModel.Commands!.PushCommand.ExecuteAsync(null);
        await viewModel.Commands.PendingOperation;
        window.UpdateLayout();

        Assert.True(viewModel.Commands.IsError);
        Assert.Contains("error", Find<TextBlock>(window, "OperationStatus").Classes);
    }

    [AvaloniaFact]
    public async Task StashesAppearInTheSidebarWithTheirDescriptions()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "work\n");
        fixture.Git("stash", "push", "--quiet", "-m", "a described stash");

        var (window, _) = await ShowAsync(fixture);

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Stashes", texts);
        Assert.Contains("a described stash", texts);
    }

    [AvaloniaFact]
    public async Task TheWholeWindowStillRendersWhileMidMerge()
    {
        using var fixture = CreateConflictingBranches();
        var (window, viewModel) = await ShowAsync(fixture);
        viewModel.Commands!.ConfirmAsync = _ => Task.FromResult(true);

        var feature = viewModel.Sections.Single(s => s.Title == "Branches")
            .Items.Single(i => i.DisplayName == "feature");

        await viewModel.Commands.MergeRefCommand.ExecuteAsync(feature);
        await viewModel.Commands.PendingOperation;
        window.UpdateLayout();

        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));
    }
}
