using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Grove.App.ViewModels;
using Grove.App.Views;
using Grove.Core;
using Grove.Core.Tests;

namespace Grove.App.Tests;

public class RebaseAndReflogUiTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static TestRepository CreateThreeCommitsOnABranch()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "base.txt", "base\n");
        fixture.Git("branch", "upstream");
        fixture.Commit("first change", "one.txt", "one\n");
        fixture.Commit("second change", "two.txt", "two\n");
        fixture.Commit("third change", "three.txt", "three\n");
        return fixture;
    }

    private static IReadOnlyList<string> Subjects(TestRepository fixture) =>
        [.. fixture.Git("log", "--format=%s", "upstream..HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    private static async Task<RebaseEditorViewModel> LoadEditorAsync(TestRepository fixture)
    {
        var vm = new RebaseEditorViewModel(await OpenAsync(fixture))
        {
            ConfirmAsync = _ => Task.FromResult(true),
        };

        await vm.LoadAsync("upstream", TestContext.Current.CancellationToken);
        return vm;
    }

    // ------------------------------------------------------- rebase editor

    [Fact]
    public async Task ThePlanIsListedOldestFirstAsPicks()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);

        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("first change", vm.Rows[0].Subject);
        Assert.All(vm.Rows, r => Assert.Equal(RebaseAction.Pick, r.Action));
        Assert.Equal("3 commits will be replayed", vm.Summary);
    }

    [Fact]
    public async Task NothingToRebaseIsExplainedRatherThanShowingAnEmptyList()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("only", "a.txt", "one\n");
        fixture.Git("branch", "upstream");

        var vm = new RebaseEditorViewModel(await OpenAsync(fixture));
        await vm.LoadAsync("upstream", TestContext.Current.CancellationToken);

        Assert.Empty(vm.Rows);
        Assert.Contains("nothing to rebase", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task MovingARowKeepsItSelectedSoRepeatedPressesWork()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);

        vm.SelectedRow = vm.Rows[2];
        vm.MoveUpCommand.Execute(null);
        vm.MoveUpCommand.Execute(null);

        // Two presses should walk it to the top, which only works if selection follows the row.
        Assert.Equal("third change", vm.Rows[0].Subject);
        Assert.Same(vm.Rows[0], vm.SelectedRow);
    }

    [Fact]
    public async Task MovingIsBlockedAtTheEnds()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);

        vm.SelectedRow = vm.Rows[0];
        Assert.False(vm.CanMoveUp);
        Assert.True(vm.CanMoveDown);

        vm.SelectedRow = vm.Rows[^1];
        Assert.True(vm.CanMoveUp);
        Assert.False(vm.CanMoveDown);
    }

    [Fact]
    public async Task ReorderingInTheEditorReordersTheRealHistory()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);

        vm.SelectedRow = vm.Rows[2];
        vm.MoveUpCommand.Execute(null);
        vm.MoveUpCommand.Execute(null);

        await vm.StartCommand.ExecuteAsync(null);
        await vm.PendingOperation;

        Assert.Equal(["second change", "first change", "third change"], Subjects(fixture));
    }

    [Fact]
    public async Task StartingIsBlockedUntilConfirmed()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);

        var asked = false;
        vm.ConfirmAsync = _ => { asked = true; return Task.FromResult(false); };
        vm.Rows[0].Action = RebaseAction.Drop;

        await vm.StartCommand.ExecuteAsync(null);
        await vm.PendingOperation;

        Assert.True(asked);
        Assert.Equal(3, Subjects(fixture).Count);
    }

    [Fact]
    public async Task StartingWithNoConfirmationHookRefusesRatherThanProceeding()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);
        vm.ConfirmAsync = null;

        await vm.StartCommand.ExecuteAsync(null);
        await vm.PendingOperation;

        // History rewriting must never happen on silence.
        Assert.Equal(3, Subjects(fixture).Count);
    }

    [Fact]
    public async Task TheConfirmationSaysHowManyCommitsAreBeingDropped()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);

        string? prompt = null;
        vm.ConfirmAsync = message => { prompt = message; return Task.FromResult(false); };
        vm.Rows[0].Action = RebaseAction.Drop;
        vm.Rows[1].Action = RebaseAction.Drop;

        await vm.StartCommand.ExecuteAsync(null);
        await vm.PendingOperation;

        Assert.Contains("dropping 2", prompt);
    }

    [Fact]
    public async Task DroppingInTheEditorDropsTheCommit()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);

        vm.Rows[1].Action = RebaseAction.Drop;
        await vm.StartCommand.ExecuteAsync(null);
        await vm.PendingOperation;

        Assert.Equal(["third change", "first change"], Subjects(fixture));
    }

    [Fact]
    public async Task SquashingInTheEditorCombinesTheCommits()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);

        vm.Rows[1].Action = RebaseAction.Squash;
        await vm.StartCommand.ExecuteAsync(null);
        await vm.PendingOperation;

        Assert.Equal(2, Subjects(fixture).Count);
    }

    [Fact]
    public async Task AnInvalidPlanIsReportedAndLeavesTheEditorUsable()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);

        vm.Rows[0].Action = RebaseAction.Squash;
        await vm.StartCommand.ExecuteAsync(null);
        await vm.PendingOperation;

        Assert.True(vm.IsError);
        Assert.Contains("nothing above it", vm.StatusText, StringComparison.Ordinal);

        // Nothing changed, so the plan can be corrected and started again.
        Assert.False(vm.HasRun);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal(3, Subjects(fixture).Count);
    }

    [Fact]
    public async Task RowsReportHowTheirActionWillReadOnScreen()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);

        vm.Rows[0].Action = RebaseAction.Drop;
        vm.Rows[1].Action = RebaseAction.Fixup;
        vm.Rows[2].Action = RebaseAction.Edit;

        Assert.True(vm.Rows[0].IsDropped);
        Assert.True(vm.Rows[1].IsCombined);
        Assert.True(vm.Rows[2].StopsHere);
    }

    [AvaloniaFact]
    public async Task TheRebaseWindowRendersItsPlanAndControls()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var vm = await LoadEditorAsync(fixture);
        var window = new RebaseEditorWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        Assert.Equal(3, Find<ListBox>(window, "RebaseList").GetRealizedContainers().Count());
        Assert.True(Find<Button>(window, "StartRebaseButton").IsEffectivelyEnabled);

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        Assert.Contains("3 commits will be replayed", texts);
        Assert.Contains("first change", texts);

        // Every row offers the full set of actions.
        Assert.Equal(3, window.GetVisualDescendants().OfType<ComboBox>().Count());
        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));
    }

    // -------------------------------------------------------------- reflog

    [Fact]
    public async Task TheReflogListsEntriesAndSummarisesThem()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var vm = new ReflogViewModel(await OpenAsync(fixture));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.Entries.Count);
        Assert.Contains("nothing is unreachable", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrphanedCommitIsFlaggedAsUnreachable()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("lost work", "a.txt", "two\n");
        fixture.Git("reset", "--hard", "HEAD~1");

        var vm = new ReflogViewModel(await OpenAsync(fixture));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Contains(vm.Entries, e => e.IsUnreachable && e.Subject == "lost work");
        Assert.Contains("1 unreachable", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUnreachableFilterHidesEverythingElse()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("lost work", "a.txt", "two\n");
        fixture.Git("reset", "--hard", "HEAD~1");

        var vm = new ReflogViewModel(await OpenAsync(fixture));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.ShowOnlyUnreachable = true;
        await vm.PendingLoad;

        Assert.All(vm.Entries, e => Assert.True(e.IsUnreachable));
    }

    [Fact]
    public async Task ADestructiveActionIsMarkedAsSuch()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");
        fixture.Git("reset", "--hard", "HEAD~1");

        var vm = new ReflogViewModel(await OpenAsync(fixture));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.Entries[0].IsPotentiallyDestructive);
    }

    [Fact]
    public async Task RecoveringOntoABranchRestoresLostWork()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("lost work", "a.txt", "two\n");
        fixture.Git("reset", "--hard", "HEAD~1");

        var vm = new ReflogViewModel(await OpenAsync(fixture))
        {
            PromptAsync = _ => Task.FromResult<string?>("recovered"),
        };
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var lost = vm.Entries.First(e => e.Subject == "lost work");
        await vm.CreateBranchCommand.ExecuteAsync(lost);
        await vm.PendingOperation;

        Assert.Equal("two\n", fixture.WorkingContent("a.txt"));
        Assert.DoesNotContain(vm.Entries, e => e.IsUnreachable);
    }

    [Fact]
    public async Task ResettingFromTheReflogIsBlockedUntilConfirmed()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var vm = new ReflogViewModel(await OpenAsync(fixture)) { ConfirmAsync = _ => Task.FromResult(false) };
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.ResetToCommand.ExecuteAsync(vm.Entries[^1]);
        await vm.PendingOperation;

        Assert.Equal("two\n", fixture.WorkingContent("a.txt"));
    }

    [Fact]
    public async Task CancellingTheBranchPromptRecoversNothing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("lost work", "a.txt", "two\n");
        fixture.Git("reset", "--hard", "HEAD~1");

        var vm = new ReflogViewModel(await OpenAsync(fixture))
        {
            PromptAsync = _ => Task.FromResult<string?>(null),
        };
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.CreateBranchCommand.ExecuteAsync(vm.Entries.First(e => e.IsUnreachable));
        await vm.PendingOperation;

        Assert.Contains(vm.Entries, e => e.IsUnreachable);
    }

    [AvaloniaFact]
    public async Task TheReflogWindowRendersItsEntriesAndActions()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("lost work", "a.txt", "two\n");
        fixture.Git("reset", "--hard", "HEAD~1");

        var vm = new ReflogViewModel(await OpenAsync(fixture));
        var window = new ReflogWindow { DataContext = vm };
        window.Show();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        window.UpdateLayout();

        Assert.Equal(vm.Entries.Count, Find<ListBox>(window, "ReflogList").GetRealizedContainers().Count());
        Assert.True(Find<Button>(window, "BranchHereButton").IsEffectivelyVisible);

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        Assert.Contains("unreachable", texts);
        Assert.Contains("reset", texts);

        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));
    }
}
