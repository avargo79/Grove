using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GitFork.App.ViewModels;
using GitFork.App.Views;
using GitFork.Core.Tests;

namespace GitFork.App.Tests;

/// <summary>Renders the staging pane headlessly and checks what a user would actually see.</summary>
public class WorkingCopyRenderTests
{
    private static TestRepository CreateDirtyRepository()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "tracked.txt", "one\n");
        fixture.WriteFile("staged.txt", "already staged\n");
        fixture.Git("add", "staged.txt");
        fixture.WriteFile("tracked.txt", "modified\n");
        fixture.WriteFile("untracked.txt", "loose\n");
        return fixture;
    }

    private static async Task<(Window Window, MainViewModel ViewModel)> ShowWorkingCopyAsync(TestRepository fixture)
    {
        var (window, _, viewModel) = await TestShell.OpenAsync(fixture.Path);

        viewModel.SelectWorkingCopy();
        window.UpdateLayout();
        return (window, viewModel);
    }

    private static IEnumerable<string> VisibleText(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))!;

    [AvaloniaFact]
    public async Task ThePinnedUncommittedRowIsVisibleWhenThereAreChanges()
    {
        using var fixture = CreateDirtyRepository();
        var (window, _) = await ShowWorkingCopyAsync(fixture);

        var row = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "UncommittedRow");

        Assert.True(row.IsVisible);
        Assert.Contains("Uncommitted changes", VisibleText(window));
        Assert.Contains("3 changed files", VisibleText(window));
    }

    [AvaloniaFact]
    public async Task ThePinnedRowIsHiddenWhenTheTreeIsClean()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var (window, _, _) = await TestShell.OpenAsync(fixture.Path);

        var row = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "UncommittedRow");
        Assert.False(row.IsVisible);
    }

    [AvaloniaFact]
    public async Task SelectingTheWorkingCopySwapsTheLowerPaneToTheStagingView()
    {
        using var fixture = CreateDirtyRepository();
        var (window, _) = await ShowWorkingCopyAsync(fixture);

        Assert.Single(window.GetVisualDescendants().OfType<WorkingCopyView>());
        Assert.Empty(window.GetVisualDescendants().OfType<CommitDetailView>());
    }

    [AvaloniaFact]
    public async Task BothFileListsRenderTheirEntries()
    {
        using var fixture = CreateDirtyRepository();
        var (window, _) = await ShowWorkingCopyAsync(fixture);

        var staged = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "StagedList");
        var unstaged = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "UnstagedList");

        Assert.Single(staged.GetRealizedContainers());
        Assert.Equal(2, unstaged.GetRealizedContainers().Count());

        var texts = VisibleText(window).ToList();
        Assert.Contains("staged.txt", texts);
        Assert.Contains("tracked.txt", texts);
        Assert.Contains("untracked.txt", texts);
    }

    [AvaloniaFact]
    public async Task TheDiffRendersHunkHeadersWithTheirOwnStagingButtons()
    {
        using var fixture = CreateDirtyRepository();
        var (window, viewModel) = await ShowWorkingCopyAsync(fixture);

        var wc = viewModel.WorkingCopy!;
        wc.SelectedFile = wc.UnstagedFiles.Single(f => f.Change.Path == "tracked.txt");
        await wc.PendingDiffLoad;
        window.UpdateLayout();

        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Select(b => b.Content as string)
            .Where(c => c is not null)
            .ToList();

        Assert.Contains("Stage Hunk", buttons);
        Assert.Contains("Unstage Hunk", buttons);
    }

    [AvaloniaFact]
    public async Task HunkButtonsSitInsideTheVisibleWidthRatherThanScrollingAway()
    {
        // Right-aligning them pushed the buttons past the diff's horizontal scroll extent.
        using var fixture = CreateDirtyRepository();
        var (window, viewModel) = await ShowWorkingCopyAsync(fixture);

        var wc = viewModel.WorkingCopy!;
        wc.SelectedFile = wc.UnstagedFiles.Single(f => f.Change.Path == "tracked.txt");
        await wc.PendingDiffLoad;
        window.UpdateLayout();

        var diffList = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "DiffList");
        var stageHunk = window.GetVisualDescendants().OfType<Button>()
            .First(b => (b.Content as string) == "Stage Hunk");

        var origin = stageHunk.TranslatePoint(new Point(0, 0), diffList)!.Value;
        Assert.InRange(origin.X, 0, diffList.Bounds.Width);
    }

    [AvaloniaFact]
    public async Task TheCommitBoxRendersAndStaysDisabledUntilAMessageIsTyped()
    {
        using var fixture = CreateDirtyRepository();
        var (window, viewModel) = await ShowWorkingCopyAsync(fixture);

        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "CommitButton");
        var box = window.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "CommitMessageBox");

        Assert.False(button.IsEffectivelyEnabled);

        viewModel.WorkingCopy!.CommitMessage = "a real message";
        window.UpdateLayout();

        Assert.Equal("a real message", box.Text);
        Assert.True(button.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task TheAmendCheckboxIsBoundToTheViewModel()
    {
        using var fixture = CreateDirtyRepository();
        var (window, viewModel) = await ShowWorkingCopyAsync(fixture);

        var checkBox = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.Name == "AmendCheckBox");
        Assert.False(checkBox.IsChecked);

        viewModel.WorkingCopy!.IsAmending = true;
        window.UpdateLayout();

        Assert.True(checkBox.IsChecked);
    }

    [AvaloniaFact]
    public async Task TheWholeStagingPaneRendersWithoutThrowing()
    {
        using var fixture = CreateDirtyRepository();
        var (window, _) = await ShowWorkingCopyAsync(fixture);

        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));
    }
}
