using Grove.Core;

namespace Grove.Core.Tests;

[Trait("Category", "Integration")]
public class GitHistoryOperationsTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    /// <summary>main and feature both edit the same line, so merging them must conflict.</summary>
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

    /// <summary>main and feature touch different files, so merging them is clean.</summary>
    private static TestRepository CreateCleanBranches()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "shared.txt", "original\n");

        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("feature work", "feature.txt", "feature\n");

        fixture.Git("checkout", "--quiet", "main");
        fixture.Commit("main work", "main.txt", "main\n");
        return fixture;
    }

    // --------------------------------------------------------------- state

    [Fact]
    public async Task AQuietRepositoryReportsNoOperationInProgress()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var state = await (await OpenAsync(fixture)).History.GetStateAsync();

        Assert.False(state.IsInProgress);
        Assert.False(state.HasConflicts);
        Assert.Equal(RepositoryOperation.None, state.Operation);
    }

    // --------------------------------------------------------------- merge

    [Fact]
    public async Task ACleanMergeSucceedsAndBringsBothSidesTogether()
    {
        using var fixture = CreateCleanBranches();
        var repo = await OpenAsync(fixture);

        var result = await repo.History.MergeAsync("feature");

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(fixture.Path, "feature.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Path, "main.txt")));
    }

    [Fact]
    public async Task AConflictingMergeIsReportedAsConflictedNotFailed()
    {
        using var fixture = CreateConflictingBranches();
        var repo = await OpenAsync(fixture);

        var result = await repo.History.MergeAsync("feature");

        // A conflict is a state to resolve, not an error to report.
        Assert.Equal(OperationOutcome.Conflicted, result.Outcome);
        Assert.Contains("shared.txt", result.ConflictedPaths);
    }

    [Fact]
    public async Task AConflictedMergeLeavesTheRepositoryMidMerge()
    {
        using var fixture = CreateConflictingBranches();
        var repo = await OpenAsync(fixture);
        await repo.History.MergeAsync("feature");

        var state = await repo.History.GetStateAsync();

        Assert.Equal(RepositoryOperation.Merge, state.Operation);
        Assert.Contains("shared.txt", state.ConflictedPaths);
        Assert.Equal("Merge in progress", state.Description);
    }

    [Fact]
    public async Task ConflictedFilesAreAlsoVisibleInStatus()
    {
        using var fixture = CreateConflictingBranches();
        var repo = await OpenAsync(fixture);
        await repo.History.MergeAsync("feature");

        var status = await repo.GetStatusAsync();

        Assert.Single(status.Unstaged, f => f.Kind == ChangeKind.Unmerged && f.Path == "shared.txt");
    }

    [Fact]
    public async Task AbortingAMergeRestoresThePreMergeState()
    {
        using var fixture = CreateConflictingBranches();
        var repo = await OpenAsync(fixture);
        await repo.History.MergeAsync("feature");

        var result = await repo.History.AbortAsync();

        Assert.True(result.Succeeded);
        Assert.False((await repo.History.GetStateAsync()).IsInProgress);
        Assert.Equal("from main\n", fixture.WorkingContent("shared.txt"));
    }

    [Fact]
    public async Task ContinuingAMergeAfterResolvingCompletesIt()
    {
        using var fixture = CreateConflictingBranches();
        var repo = await OpenAsync(fixture);
        await repo.History.MergeAsync("feature");

        fixture.WriteFile("shared.txt", "resolved by hand\n");
        fixture.Git("add", "shared.txt");

        var result = await repo.History.ContinueAsync();

        Assert.True(result.Succeeded);
        Assert.False((await repo.History.GetStateAsync()).IsInProgress);
        Assert.Single(await repo.GetCommitsAsync(), c => c.IsMerge);
    }

    [Fact]
    public async Task ContinuingWhileConflictsRemainRefusesRatherThanCommittingMarkers()
    {
        using var fixture = CreateConflictingBranches();
        var repo = await OpenAsync(fixture);
        await repo.History.MergeAsync("feature");

        var result = await repo.History.ContinueAsync();

        Assert.Equal(OperationOutcome.Conflicted, result.Outcome);
        Assert.Contains("Resolve the remaining conflicts", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinuingOrAbortingWithNothingInProgressIsRefused()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        var repo = await OpenAsync(fixture);

        Assert.Equal(OperationOutcome.Failed, (await repo.History.ContinueAsync()).Outcome);
        Assert.Equal(OperationOutcome.Failed, (await repo.History.AbortAsync()).Outcome);
    }

    [Fact]
    public async Task NoFastForwardForcesAMergeCommitEvenWhenNotNeeded()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "a.txt", "one\n");
        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("ahead", "b.txt", "two\n");
        fixture.Git("checkout", "--quiet", "main");

        var repo = await OpenAsync(fixture);
        await repo.History.MergeAsync("feature", noFastForward: true);

        Assert.Single(await repo.GetCommitsAsync(), c => c.IsMerge);
    }

    // -------------------------------------------------------------- rebase

    [Fact]
    public async Task ACleanRebaseReplaysTheBranchOnTop()
    {
        using var fixture = CreateCleanBranches();
        fixture.Git("checkout", "--quiet", "feature");

        var repo = await OpenAsync(fixture);
        var result = await repo.History.RebaseAsync("main");

        Assert.True(result.Succeeded);
        // Replayed onto main, so main's file is now present on feature too.
        Assert.True(File.Exists(Path.Combine(fixture.Path, "main.txt")));
        Assert.DoesNotContain(await repo.GetCommitsAsync(), c => c.IsMerge);
    }

    [Fact]
    public async Task AConflictingRebaseLeavesTheRepositoryMidRebase()
    {
        using var fixture = CreateConflictingBranches();
        fixture.Git("checkout", "--quiet", "feature");

        var repo = await OpenAsync(fixture);
        var result = await repo.History.RebaseAsync("main");

        Assert.Equal(OperationOutcome.Conflicted, result.Outcome);
        Assert.Equal(RepositoryOperation.Rebase, (await repo.History.GetStateAsync()).Operation);
    }

    [Fact]
    public async Task AbortingARebaseRestoresTheBranch()
    {
        using var fixture = CreateConflictingBranches();
        fixture.Git("checkout", "--quiet", "feature");

        var repo = await OpenAsync(fixture);
        await repo.History.RebaseAsync("main");
        var result = await repo.History.AbortAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("feature", await repo.GetCurrentBranchAsync());
        Assert.Equal("from feature\n", fixture.WorkingContent("shared.txt"));
    }

    [Fact]
    public async Task ContinuingARebaseAfterResolvingCompletesIt()
    {
        // Without editor suppression this would block forever waiting for a commit message.
        using var fixture = CreateConflictingBranches();
        fixture.Git("checkout", "--quiet", "feature");

        var repo = await OpenAsync(fixture);
        await repo.History.RebaseAsync("main");

        fixture.WriteFile("shared.txt", "resolved during rebase\n");
        fixture.Git("add", "shared.txt");

        var result = await repo.History.ContinueAsync();

        Assert.True(result.Succeeded);
        Assert.False((await repo.History.GetStateAsync()).IsInProgress);
    }

    // --------------------------------------------------- cherry-pick/revert

    [Fact]
    public async Task CherryPickingBringsACommitOntoTheCurrentBranch()
    {
        using var fixture = CreateCleanBranches();
        var featureSha = fixture.Git("rev-parse", "feature").Trim();

        var repo = await OpenAsync(fixture);
        var result = await repo.History.CherryPickAsync(featureSha);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(fixture.Path, "feature.txt")));
        Assert.Contains(await repo.GetCommitsAsync(), c => c.Subject == "feature work");
    }

    [Fact]
    public async Task AConflictingCherryPickIsReportedAsConflicted()
    {
        using var fixture = CreateConflictingBranches();
        var featureSha = fixture.Git("rev-parse", "feature").Trim();

        var repo = await OpenAsync(fixture);
        var result = await repo.History.CherryPickAsync(featureSha);

        Assert.Equal(OperationOutcome.Conflicted, result.Outcome);
        Assert.Equal(RepositoryOperation.CherryPick, (await repo.History.GetStateAsync()).Operation);
    }

    [Fact]
    public async Task RevertingAddsAnInverseCommitRatherThanRewritingHistory()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        var second = fixture.Commit("add a file", "b.txt", "two\n");

        var repo = await OpenAsync(fixture);
        var result = await repo.History.RevertAsync(second);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(fixture.Path, "b.txt")));
        Assert.Equal(3, fixture.CommitCount());
    }

    [Fact]
    public async Task RevertingAMergeNeedsAMainlineParent()
    {
        using var fixture = CreateCleanBranches();
        var repo = await OpenAsync(fixture);
        await repo.History.MergeAsync("feature", noFastForward: true);
        var mergeSha = await repo.GetHeadShaAsync();

        // Without -m git refuses, because it cannot know which side to keep.
        Assert.Equal(OperationOutcome.Failed, (await repo.History.RevertAsync(mergeSha!)).Outcome);
        Assert.True((await repo.History.RevertAsync(mergeSha!, mainlineParent: 1)).Succeeded);
    }

    // --------------------------------------------------------------- reset

    [Fact]
    public async Task ASoftResetMovesHeadAndKeepsChangesStaged()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var repo = await OpenAsync(fixture);
        var result = await repo.History.ResetAsync(first, ResetMode.Soft);

        Assert.True(result.Succeeded);
        Assert.Equal(first, await repo.GetHeadShaAsync());
        Assert.Single((await repo.GetStatusAsync()).Staged, f => f.Path == "a.txt");
        Assert.Equal("two\n", fixture.WorkingContent("a.txt"));
    }

    [Fact]
    public async Task AMixedResetLeavesChangesUnstagedInTheWorkingTree()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var repo = await OpenAsync(fixture);
        await repo.History.ResetAsync(first, ResetMode.Mixed);

        var status = await repo.GetStatusAsync();
        Assert.Empty(status.Staged);
        Assert.Single(status.Unstaged, f => f.Path == "a.txt");
        Assert.Equal("two\n", fixture.WorkingContent("a.txt"));
    }

    [Fact]
    public async Task AHardResetThrowsTheChangesAwayEntirely()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var repo = await OpenAsync(fixture);
        await repo.History.ResetAsync(first, ResetMode.Hard);

        Assert.True((await repo.GetStatusAsync()).IsClean);
        Assert.Equal("one\n", fixture.WorkingContent("a.txt"));
    }
}
