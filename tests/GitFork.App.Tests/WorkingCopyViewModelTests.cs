using GitFork.App.ViewModels;
using GitFork.Core;
using GitFork.Core.Tests;

namespace GitFork.App.Tests;

/// <summary>
/// Drives the staging pane end to end against real repositories: what the user clicks, and what
/// ends up in the index afterwards.
/// </summary>
public class WorkingCopyViewModelTests
{
    private static MainViewModel NewViewModel() => new() { WatchForChanges = false };

    /// <summary>One staged file, one modified file and one untracked file.</summary>
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

    private static async Task<MainViewModel> LoadAsync(TestRepository fixture)
    {
        var vm = NewViewModel();
        await vm.LoadRepositoryAsync(fixture.Path);
        await vm.PendingDetailLoad;
        await vm.PendingDiffLoad;
        return vm;
    }

    /// <summary>Waits for the reload triggered by a staging command to settle.</summary>
    private static async Task SettleAsync(MainViewModel vm)
    {
        await Task.Yield();
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.PendingDetailLoad;
        if (vm.WorkingCopy is { } wc)
            await wc.PendingDiffLoad;
    }

    // ------------------------------------------------------- pinned row

    [Fact]
    public async Task ADirtyRepositoryReportsUncommittedChanges()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);

        Assert.True(vm.HasUncommittedChanges);
        Assert.Equal("3 changed files", vm.UncommittedSummary);
    }

    [Fact]
    public async Task ACleanRepositoryHasNoUncommittedChangesRow()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var vm = await LoadAsync(fixture);

        Assert.False(vm.HasUncommittedChanges);
    }

    [Fact]
    public async Task SelectingTheWorkingCopyHandsTheLowerPaneToIt()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);

        Assert.Same(vm.Detail, vm.DetailContent);

        vm.SelectWorkingCopy();

        Assert.True(vm.IsWorkingCopySelected);
        Assert.Same(vm.WorkingCopy, vm.DetailContent);
        Assert.Null(vm.SelectedCommit);
    }

    [Fact]
    public async Task SelectingACommitTakesThePaneBackFromTheWorkingCopy()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        vm.SelectWorkingCopy();

        vm.SelectedCommit = vm.Commits[0];
        await vm.PendingDetailLoad;

        Assert.False(vm.IsWorkingCopySelected);
        Assert.Same(vm.Detail, vm.DetailContent);
    }

    [Fact]
    public async Task ARefreshDoesNotThrowTheUserOutOfTheWorkingCopyPane()
    {
        // The file watcher refreshes on every save, so this would otherwise bounce the user
        // back to the commit view mid-edit.
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        vm.SelectWorkingCopy();

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.PendingDetailLoad;

        Assert.True(vm.IsWorkingCopySelected);
        Assert.Same(vm.WorkingCopy, vm.DetailContent);
    }

    // ---------------------------------------------------------- file lists

    [Fact]
    public async Task FilesAreSplitAcrossTheStagedAndUnstagedLists()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        Assert.Single(wc.StagedFiles, f => f.Change.Path == "staged.txt");
        Assert.Single(wc.UnstagedFiles, f => f.Change.Path == "tracked.txt" && !f.IsUntracked);
        Assert.Single(wc.UnstagedFiles, f => f.Change.Path == "untracked.txt" && f.IsUntracked);
    }

    [Fact]
    public async Task StagingAFileMovesItAcrossTheLists()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        var file = wc.UnstagedFiles.Single(f => f.Change.Path == "tracked.txt");
        await wc.StageFileCommand.ExecuteAsync(file);
        await SettleAsync(vm);

        wc = vm.WorkingCopy!;
        Assert.Single(wc.StagedFiles, f => f.Change.Path == "tracked.txt");
        Assert.DoesNotContain(wc.UnstagedFiles, f => f.Change.Path == "tracked.txt");
        Assert.Equal("modified\n", fixture.IndexContent("tracked.txt"));
    }

    [Fact]
    public async Task UnstagingAFileMovesItBack()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        await wc.UnstageFileCommand.ExecuteAsync(wc.StagedFiles.Single());
        await SettleAsync(vm);

        wc = vm.WorkingCopy!;
        Assert.Empty(wc.StagedFiles);
        Assert.Contains(wc.UnstagedFiles, f => f.Change.Path == "staged.txt");
    }

    [Fact]
    public async Task StageAllEmptiesTheUnstagedList()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);

        await vm.WorkingCopy!.StageAllCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.Empty(vm.WorkingCopy!.UnstagedFiles);
        Assert.Equal(3, vm.WorkingCopy.StagedFiles.Count);
    }

    [Fact]
    public async Task UnstageAllEmptiesTheStagedList()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);

        await vm.WorkingCopy!.StageAllCommand.ExecuteAsync(null);
        await SettleAsync(vm);
        await vm.WorkingCopy!.UnstageAllCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.Empty(vm.WorkingCopy!.StagedFiles);
        Assert.Equal(3, vm.WorkingCopy.UnstagedFiles.Count);
    }

    // ----------------------------------------------------------- the diff

    [Fact]
    public async Task SelectingAFileShowsItsHunksAndLines()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.SelectedFile = wc.UnstagedFiles.Single(f => f.Change.Path == "tracked.txt");
        await wc.PendingDiffLoad;

        Assert.Contains(wc.DiffRows, r => r.IsHunkHeader);
        Assert.Contains(wc.DiffRows, r => r.IsAdded && r.Text == "modified");
        Assert.Contains(wc.DiffRows, r => r.IsRemoved && r.Text == "one");
    }

    [Fact]
    public async Task TheDiffIsNotDuplicatedWhenLoadAndSelectionOverlap()
    {
        // Loading assigns SelectedFile, which itself triggers a reload; both running would
        // append the same rows twice.
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.SelectedFile = wc.UnstagedFiles.Single(f => f.Change.Path == "tracked.txt");
        await wc.PendingDiffLoad;

        Assert.Single(wc.DiffRows, r => r.IsHunkHeader);
        Assert.Single(wc.DiffRows, r => r.IsAdded && r.Text == "modified");
    }

    [Fact]
    public async Task AnUntrackedFileShowsItsContentAsAllAdditions()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.SelectedFile = wc.UnstagedFiles.Single(f => f.IsUntracked);
        await wc.PendingDiffLoad;

        Assert.Contains(wc.DiffRows, r => r.IsAdded && r.Text == "loose");
        Assert.DoesNotContain(wc.DiffRows, r => r.IsRemoved);
    }

    [Fact]
    public async Task AStagedFileIsDiffedAgainstHeadNotTheWorkingTree()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.SelectedFile = wc.StagedFiles.Single();
        await wc.PendingDiffLoad;

        Assert.Contains(wc.DiffRows, r => r.IsAdded && r.Text == "already staged");
    }

    [Fact]
    public async Task OnlyAdditionsAndRemovalsAreStageableRows()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.SelectedFile = wc.UnstagedFiles.Single(f => f.Change.Path == "tracked.txt");
        await wc.PendingDiffLoad;

        Assert.All(wc.DiffRows.Where(r => r.IsStageable),
            r => Assert.True(r.IsAdded || r.IsRemoved));
        Assert.DoesNotContain(wc.DiffRows.Where(r => r.IsHunkHeader), r => r.IsStageable);
    }

    // -------------------------------------------------- hunk/line staging

    /// <summary>Edits far apart in one file, so git emits two separate hunks.</summary>
    private static TestRepository CreateTwoHunkRepository()
    {
        var fixture = TestRepository.CreateEmpty();
        var original = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        fixture.Commit("first", "a.txt", original);
        fixture.WriteFile("a.txt", original
            .Replace("line 3\n", "LINE THREE\n", StringComparison.Ordinal)
            .Replace("line 18\n", "LINE EIGHTEEN\n", StringComparison.Ordinal));
        return fixture;
    }

    [Fact]
    public async Task StagingOneHunkFromTheDiffLeavesTheOther()
    {
        using var fixture = CreateTwoHunkRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.SelectedFile = wc.UnstagedFiles.Single();
        await wc.PendingDiffLoad;

        var firstHunkHeader = wc.DiffRows.First(r => r.IsHunkHeader);
        await wc.StageHunkCommand.ExecuteAsync(firstHunkHeader);
        await SettleAsync(vm);

        var index = fixture.IndexContent("a.txt");
        Assert.Contains("LINE THREE", index, StringComparison.Ordinal);
        Assert.DoesNotContain("LINE EIGHTEEN", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnstagingOneHunkLeavesTheOtherStaged()
    {
        using var fixture = CreateTwoHunkRepository();
        fixture.Git("add", "a.txt");

        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;
        wc.SelectedFile = wc.StagedFiles.Single();
        await wc.PendingDiffLoad;

        await wc.UnstageHunkCommand.ExecuteAsync(wc.DiffRows.First(r => r.IsHunkHeader));
        await SettleAsync(vm);

        var index = fixture.IndexContent("a.txt");
        Assert.Contains("line 3\n", index, StringComparison.Ordinal);
        Assert.Contains("LINE EIGHTEEN", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StagingHighlightedLinesStagesOnlyThoseLines()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "keep\n");
        fixture.WriteFile("a.txt", "keep\nfirst added\nsecond added\n");

        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;
        wc.SelectedFile = wc.UnstagedFiles.Single();
        await wc.PendingDiffLoad;

        // Highlight just the first addition, as clicking that row in the diff would.
        wc.SelectedRows.Clear();
        wc.SelectedRows.Add(wc.DiffRows.First(r => r.IsAdded));
        await wc.StageSelectedLinesCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.Equal("keep\nfirst added\n", fixture.IndexContent("a.txt"));
        Assert.Equal("keep\nfirst added\nsecond added\n", fixture.WorkingContent("a.txt"));
    }

    [Fact]
    public async Task StagingWithNothingHighlightedDoesNothing()
    {
        using var fixture = CreateTwoHunkRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.SelectedFile = wc.UnstagedFiles.Single();
        await wc.PendingDiffLoad;
        wc.SelectedRows.Clear();

        await wc.StageSelectedLinesCommand.ExecuteAsync(null);

        Assert.Empty(vm.Status.Staged);
    }

    [Fact]
    public async Task HunkHeaderRowsAreIgnoredByLineStaging()
    {
        using var fixture = CreateTwoHunkRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.SelectedFile = wc.UnstagedFiles.Single();
        await wc.PendingDiffLoad;

        wc.SelectedRows.Clear();
        wc.SelectedRows.Add(wc.DiffRows.First(r => r.IsHunkHeader));
        await wc.StageSelectedLinesCommand.ExecuteAsync(null);

        Assert.Empty(vm.Status.Staged);
    }

    // -------------------------------------------------------- discarding

    [Fact]
    public async Task DiscardingIsBlockedUntilTheUserConfirms()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        var asked = false;
        wc.ConfirmDiscardAsync = _ => { asked = true; return Task.FromResult(false); };

        await wc.DiscardFileCommand.ExecuteAsync(
            wc.UnstagedFiles.Single(f => f.Change.Path == "tracked.txt"));

        Assert.True(asked);
        Assert.Equal("modified\n", fixture.WorkingContent("tracked.txt"));
    }

    [Fact]
    public async Task DiscardingWithNoConfirmationHookRefusesRatherThanProceeding()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;
        wc.ConfirmDiscardAsync = null;

        await wc.DiscardFileCommand.ExecuteAsync(
            wc.UnstagedFiles.Single(f => f.Change.Path == "tracked.txt"));

        // Silence must never be read as consent for something unrecoverable.
        Assert.Equal("modified\n", fixture.WorkingContent("tracked.txt"));
    }

    [Fact]
    public async Task ConfirmingADiscardRevertsTheFile()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;
        wc.ConfirmDiscardAsync = _ => Task.FromResult(true);

        await wc.DiscardFileCommand.ExecuteAsync(
            wc.UnstagedFiles.Single(f => f.Change.Path == "tracked.txt"));
        await SettleAsync(vm);

        Assert.Equal("one\n", fixture.WorkingContent("tracked.txt"));
    }

    [Fact]
    public async Task ConfirmingADiscardOfAnUntrackedFileDeletesIt()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        string? prompt = null;
        wc.ConfirmDiscardAsync = message => { prompt = message; return Task.FromResult(true); };

        await wc.DiscardFileCommand.ExecuteAsync(wc.UnstagedFiles.Single(f => f.IsUntracked));
        await SettleAsync(vm);

        Assert.False(File.Exists(Path.Combine(fixture.Path, "untracked.txt")));
        // The wording must say "delete", not "discard", for a file git has never seen.
        Assert.Contains("Delete the untracked file", prompt);
    }

    // --------------------------------------------------------- committing

    [Fact]
    public async Task CommittingIsBlockedUntilSomethingIsStagedAndAMessageIsTyped()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.CommitMessage = string.Empty;
        Assert.False(wc.CommitCommand.CanExecute(null));

        wc.CommitMessage = "a message";
        Assert.True(wc.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task CommittingWithNothingStagedIsBlocked()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "two\n");

        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;
        wc.CommitMessage = "nothing staged";

        Assert.False(wc.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task CommittingWritesTheCommitAndClearsTheBox()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.CommitMessage = "stage and commit";
        await wc.CommitCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.Equal(2, fixture.CommitCount());
        Assert.Equal("stage and commit", vm.Commits[0].Subject);
        Assert.Equal(string.Empty, vm.WorkingCopy!.CommitMessage);
    }

    [Fact]
    public async Task CommittingRefreshesTheHistoryList()
    {
        using var fixture = CreateDirtyRepository();
        var vm = await LoadAsync(fixture);
        var before = vm.Commits.Count;

        vm.WorkingCopy!.CommitMessage = "another commit";
        await vm.WorkingCopy.CommitCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.Equal(before + 1, vm.Commits.Count);
    }

    [Fact]
    public async Task TurningOnAmendOffersTheHeadMessage()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("the original message", "a.txt", "one\n");

        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        // Toggling amend kicks off an async prefill from HEAD.
        wc.IsAmending = true;
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal("the original message", wc.CommitMessage.Trim());
        Assert.Equal("Amend Commit", wc.CommitButtonText);
    }

    [Fact]
    public async Task AmendingRewritesHeadRatherThanAddingACommit()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("typo in mesage", "b.txt", "two\n");

        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.IsAmending = true;
        wc.CommitMessage = "typo in message";
        await wc.CommitCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.Equal(2, fixture.CommitCount());
        Assert.Equal("typo in message", vm.Commits[0].Subject);
    }

    [Fact]
    public async Task AmendingIsAllowedWithNothingStaged()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("only commit", "a.txt", "one\n");

        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        wc.IsAmending = true;
        wc.CommitMessage = "reworded";

        // Rewording HEAD needs no staged changes at all.
        Assert.True(wc.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecentMessagesArePresentedForReuse()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("earlier subject", "a.txt", "one\n");
        fixture.WriteFile("b.txt", "two\n");
        fixture.Git("add", "-A");

        var vm = await LoadAsync(fixture);
        var wc = vm.WorkingCopy!;

        Assert.Contains("earlier subject", wc.RecentMessages);

        wc.UseRecentMessageCommand.Execute("earlier subject");
        Assert.Equal("earlier subject", wc.CommitMessage);
    }

    [Fact]
    public async Task CommittingEverythingLeavesTheWorkingCopyPane()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "two\n");
        fixture.Git("add", "-A");

        var vm = await LoadAsync(fixture);
        vm.SelectWorkingCopy();

        vm.WorkingCopy!.CommitMessage = "all of it";
        await vm.WorkingCopy.CommitCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        // Nothing left to stage, so the pane falls back to showing the new commit.
        Assert.False(vm.HasUncommittedChanges);
        Assert.False(vm.IsWorkingCopySelected);
        Assert.NotNull(vm.Detail);
    }

}
