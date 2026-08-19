using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GitFork.App.Controls;
using GitFork.App.ViewModels;
using GitFork.App.Views;
using GitFork.Core;
using GitFork.Core.Tests;

namespace GitFork.App.Tests;

public class BlameAndHistoryTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    /// <summary>Two commits, so the file has lines from each.</summary>
    private static TestRepository CreateTwoAuthoredCommits()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("wrote the first lines", "Program.cs", "var one = 1;\nvar two = 2;\n");
        fixture.Commit("added a third", "Program.cs", "var one = 1;\nvar two = 2;\nvar three = 3;\n");
        return fixture;
    }

    // -------------------------------------------------------------- blame

    [Fact]
    public async Task BlameListsEveryLineWithItsCommit()
    {
        using var fixture = CreateTwoAuthoredCommits();
        var vm = new BlameViewModel(await OpenAsync(fixture));

        await vm.LoadAsync("Program.cs", ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, vm.Lines.Count);
        Assert.Equal("wrote the first lines", vm.Lines[0].Line.Summary);
        Assert.Equal("added a third", vm.Lines[2].Line.Summary);
    }

    [Fact]
    public async Task AttributionIsShownOnlyWhereItChanges()
    {
        using var fixture = CreateTwoAuthoredCommits();
        var vm = new BlameViewModel(await OpenAsync(fixture));

        await vm.LoadAsync("Program.cs", ct: TestContext.Current.CancellationToken);

        // Repeating the same sha down a block is noise; it appears once per block.
        Assert.True(vm.Lines[0].StartsBlock);
        Assert.False(vm.Lines[1].StartsBlock);
        Assert.Equal(string.Empty, vm.Lines[1].Sha);
        Assert.True(vm.Lines[2].StartsBlock);
    }

    [Fact]
    public async Task BlameLinesCarrySyntaxColouredRuns()
    {
        using var fixture = CreateTwoAuthoredCommits();
        var vm = new BlameViewModel(await OpenAsync(fixture));

        await vm.LoadAsync("Program.cs", ct: TestContext.Current.CancellationToken);

        Assert.Contains(vm.Lines[0].Runs, r => r is { Text: "var", Token: TokenKind.Keyword });
    }

    [Fact]
    public async Task BlameSummarisesHowManyCommitsItSpans()
    {
        using var fixture = CreateTwoAuthoredCommits();
        var vm = new BlameViewModel(await OpenAsync(fixture));

        await vm.LoadAsync("Program.cs", ct: TestContext.Current.CancellationToken);

        Assert.Equal("3 lines from 2 commits", vm.StatusText);
    }

    [Fact]
    public async Task BlameOfAnUnknownFileSaysSoRatherThanShowingNothing()
    {
        using var fixture = CreateTwoAuthoredCommits();
        var vm = new BlameViewModel(await OpenAsync(fixture));

        await vm.LoadAsync("nope.cs", ct: TestContext.Current.CancellationToken);

        Assert.Empty(vm.Lines);
        Assert.Equal("No blame information for this file.", vm.StatusText);
    }

    [Fact]
    public async Task BlameAtARevisionSaysWhichOneInItsTitle()
    {
        using var fixture = CreateTwoAuthoredCommits();
        var first = fixture.Git("rev-parse", "HEAD~1").Trim();
        var vm = new BlameViewModel(await OpenAsync(fixture));

        await vm.LoadAsync("Program.cs", first, TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.Lines.Count);
        Assert.Contains(first[..7], vm.Title, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task TheBlameWindowRendersItsLines()
    {
        using var fixture = CreateTwoAuthoredCommits();
        var vm = new BlameViewModel(await OpenAsync(fixture));
        var window = new BlameWindow { DataContext = vm };
        window.Show();

        await vm.LoadAsync("Program.cs", ct: TestContext.Current.CancellationToken);
        window.UpdateLayout();

        var list = Find<ListBox>(window, "BlameList");
        Assert.Equal(3, list.GetRealizedContainers().Count());

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        Assert.Contains("Test Author", texts);
        Assert.Contains("3 lines from 2 commits", texts);

        // The code itself goes through the run-aware control.
        Assert.NotEmpty(window.GetVisualDescendants().OfType<DiffTextBlock>());
        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));
    }

    // ------------------------------------------------------- file history

    [Fact]
    public async Task FileHistoryListsTheCommitsThatTouchedThePath()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("touches a", "a.txt", "one\n");
        fixture.Commit("touches b", "b.txt", "two\n");
        fixture.Commit("touches a again", "a.txt", "three\n");

        var vm = new FileHistoryViewModel(await OpenAsync(fixture));
        await vm.LoadAsync("a.txt", ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.Entries.Count);
        Assert.Equal("touches a again", vm.Entries[0].Subject);
        Assert.Equal("2 commits touched this file", vm.StatusText);
    }

    [Fact]
    public async Task TheNewestCommitIsSelectedAndItsDiffLoaded()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "Program.cs", "var total = 1;\n");
        fixture.Commit("second", "Program.cs", "var count = 1;\n");

        var vm = new FileHistoryViewModel(await OpenAsync(fixture));
        await vm.LoadAsync("Program.cs", ct: TestContext.Current.CancellationToken);

        Assert.Equal("second", vm.SelectedEntry!.Subject);
        Assert.Contains(vm.Diff.UnifiedRows, r => r.IsAdded && r.Text == "var count = 1;");
    }

    [Fact]
    public async Task SelectingAnOlderCommitShowsThatCommitsDiff()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "Program.cs", "var total = 1;\n");
        fixture.Commit("second", "Program.cs", "var count = 1;\n");

        var vm = new FileHistoryViewModel(await OpenAsync(fixture));
        await vm.LoadAsync("Program.cs", ct: TestContext.Current.CancellationToken);

        vm.SelectedEntry = vm.Entries.Single(e => e.Subject == "first");
        await vm.PendingDiffLoad;

        Assert.Contains(vm.Diff.UnifiedRows, r => r.IsAdded && r.Text == "var total = 1;");
    }

    [Fact]
    public async Task FollowingRenamesCanBeTurnedOff()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("created under the old name", "old.txt", new string('x', 200));
        fixture.Git("mv", "old.txt", "new.txt");
        fixture.CommitAll("renamed it");

        var vm = new FileHistoryViewModel(await OpenAsync(fixture));
        await vm.LoadAsync("new.txt", ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, vm.Entries.Count);

        vm.FollowRenames = false;
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Single(vm.Entries);
    }

    [Fact]
    public async Task AFileWithOneCommitIsDescribedInTheSingular()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("only", "a.txt", "one\n");

        var vm = new FileHistoryViewModel(await OpenAsync(fixture));
        await vm.LoadAsync("a.txt", ct: TestContext.Current.CancellationToken);

        Assert.Equal("1 commit touched this file", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task TheFileHistoryWindowRendersItsListAndDiff()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "Program.cs", "var total = 1;\n");
        fixture.Commit("second", "Program.cs", "var count = 1;\n");

        var vm = new FileHistoryViewModel(await OpenAsync(fixture));
        var window = new FileHistoryWindow { DataContext = vm };
        window.Show();

        await vm.LoadAsync("Program.cs", ct: TestContext.Current.CancellationToken);
        window.UpdateLayout();

        Assert.Equal(2, Find<ListBox>(window, "HistoryList").GetRealizedContainers().Count());

        // The same diff pane as the main window, so both layouts are available here too.
        Assert.Single(window.GetVisualDescendants().OfType<DiffView>());
        Assert.True(Find<ListBox>(window, "UnifiedList").IsVisible);

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        Assert.Contains("second", texts);
        Assert.Contains("2 commits touched this file", texts);

        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));
    }

    [AvaloniaFact]
    public async Task TheHistoryWindowsDiffCanSwitchToSideBySide()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "Program.cs", "var total = 1;\n");
        fixture.Commit("second", "Program.cs", "var count = 1;\n");

        var vm = new FileHistoryViewModel(await OpenAsync(fixture));
        var window = new FileHistoryWindow { DataContext = vm };
        window.Show();
        await vm.LoadAsync("Program.cs", ct: TestContext.Current.CancellationToken);
        window.UpdateLayout();

        vm.Diff.Mode = DiffViewMode.SideBySide;
        window.UpdateLayout();

        Assert.True(Find<ListBox>(window, "SideBySideList").IsVisible);
        Assert.False(Find<ListBox>(window, "UnifiedList").IsVisible);
    }
}
