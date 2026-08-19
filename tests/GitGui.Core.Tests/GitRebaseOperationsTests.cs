using GitGui.Core;

namespace GitGui.Core.Tests;

/// <summary>
/// Interactive rebase against real git. Every one of these would hang forever without the editor
/// suppression and the generated sequence editor, so they are also a regression test for that.
/// </summary>
[Trait("Category", "Integration")]
public class GitRebaseOperationsTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    /// <summary>A base commit plus three commits on top, each touching its own file.</summary>
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

    // --------------------------------------------------------------- plan

    [Fact]
    public async Task ThePlanListsTheCommitsToReplayOldestFirst()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);

        var plan = await repo.Rebase.GetTodoAsync("upstream");

        Assert.Equal(3, plan.Count);
        Assert.Equal("first change", plan[0].Subject);
        Assert.Equal("third change", plan[2].Subject);
        Assert.All(plan, i => Assert.Equal(RebaseAction.Pick, i.Action));
    }

    [Fact]
    public async Task ThePlanIsEmptyWhenThereIsNothingToReplay()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("only", "a.txt", "one\n");
        fixture.Git("branch", "upstream");

        Assert.Empty(await (await OpenAsync(fixture)).Rebase.GetTodoAsync("upstream"));
    }

    [Fact]
    public void ThePlanFileUsesTheKeywordsGitExpects()
    {
        var file = GitRebaseOperations.BuildTodoFile(
        [
            new RebaseTodoItem(RebaseAction.Pick, "aaa", "keep this"),
            new RebaseTodoItem(RebaseAction.Squash, "bbb", "fold in"),
            new RebaseTodoItem(RebaseAction.Fixup, "ccc", "fold in quietly"),
            new RebaseTodoItem(RebaseAction.Edit, "ddd", "stop here"),
        ]);

        Assert.Equal(
            "pick aaa keep this\nsquash bbb fold in\nfixup ccc fold in quietly\nedit ddd stop here\n",
            file);
    }

    [Theory]
    [InlineData("/tmp/plain/todo", "cp \"/tmp/plain/todo\"")]
    [InlineData("/tmp/with space/todo", "cp \"/tmp/with space/todo\"")]
    [InlineData("C:/Users/me/AppData/Local/Temp/x/todo", "cp \"C:/Users/me/AppData/Local/Temp/x/todo\"")]
    public void TheSequenceEditorIsOneQuotedCommandOnEveryPlatform(string path, string expected)
    {
        // git runs this through a shell and appends its own plan file, so quoting is what stops a
        // path with a space in it from silently becoming two arguments. This is also the Windows
        // path, which cannot be exercised end to end from a Mac.
        Assert.Equal(expected, GitRebaseOperations.BuildSequenceEditorCommand(path));
    }

    [Fact]
    public void RewordIsWrittenAsEditSoTheMessageIsNotSilentlyKept()
    {
        // git's own "reword" opens an editor; with editors suppressed it would keep the old
        // message without saying so.
        var file = GitRebaseOperations.BuildTodoFile(
            [new RebaseTodoItem(RebaseAction.Reword, "aaa", "rename me")]);

        Assert.StartsWith("edit ", file, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- reordering

    [Fact]
    public async Task ReorderingThePlanReordersTheCommits()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        var plan = (await repo.Rebase.GetTodoAsync("upstream")).ToList();

        // Move the third commit to the front.
        var moved = new List<RebaseTodoItem> { plan[2], plan[0], plan[1] };
        var result = await repo.Rebase.RunInteractiveAsync("upstream", moved);

        Assert.True(result.Succeeded);
        Assert.Equal(["second change", "first change", "third change"], Subjects(fixture));
    }

    // ---------------------------------------------------------------- drop

    [Fact]
    public async Task DroppingACommitLeavesItOutOfTheHistory()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        var plan = (await repo.Rebase.GetTodoAsync("upstream")).ToList();

        plan[1] = plan[1] with { Action = RebaseAction.Drop };
        var result = await repo.Rebase.RunInteractiveAsync("upstream", plan);

        Assert.True(result.Succeeded);
        Assert.Equal(["third change", "first change"], Subjects(fixture));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "two.txt")));
    }

    [Fact]
    public async Task DroppingEverythingIsRefusedBeforeGitIsCalled()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        var plan = (await repo.Rebase.GetTodoAsync("upstream"))
            .Select(i => i with { Action = RebaseAction.Drop }).ToList();

        var result = await repo.Rebase.RunInteractiveAsync("upstream", plan);

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Equal(3, Subjects(fixture).Count);
    }

    // -------------------------------------------------------------- squash

    [Fact]
    public async Task SquashingCombinesACommitIntoTheOneAboveIt()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        var plan = (await repo.Rebase.GetTodoAsync("upstream")).ToList();

        plan[1] = plan[1] with { Action = RebaseAction.Squash };
        var result = await repo.Rebase.RunInteractiveAsync("upstream", plan);

        Assert.True(result.Succeeded);
        Assert.Equal(2, Subjects(fixture).Count);

        // The squashed commit's content survives even though its own commit does not.
        Assert.True(File.Exists(Path.Combine(fixture.Path, "two.txt")));
    }

    [Fact]
    public async Task FixupCombinesWithoutKeepingTheMessage()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        var plan = (await repo.Rebase.GetTodoAsync("upstream")).ToList();

        plan[1] = plan[1] with { Action = RebaseAction.Fixup };
        var result = await repo.Rebase.RunInteractiveAsync("upstream", plan);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("second change", fixture.Git("log", "--format=%B"), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Path, "two.txt")));
    }

    [Fact]
    public async Task SquashingTheFirstCommitIsRefusedWithAnExplanation()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        var plan = (await repo.Rebase.GetTodoAsync("upstream")).ToList();

        plan[0] = plan[0] with { Action = RebaseAction.Squash };
        var result = await repo.Rebase.RunInteractiveAsync("upstream", plan);

        // git would fail confusingly here; catching it first explains why.
        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Contains("nothing above it", result.Message, StringComparison.Ordinal);
        Assert.Equal(3, Subjects(fixture).Count);
    }

    [Fact]
    public async Task DroppingTheFirstCommitStillAllowsASquashBelowIt()
    {
        // The check is about the plan after drops are removed, not the original list.
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        var plan = (await repo.Rebase.GetTodoAsync("upstream")).ToList();

        plan[0] = plan[0] with { Action = RebaseAction.Drop };
        plan[1] = plan[1] with { Action = RebaseAction.Pick };
        plan[2] = plan[2] with { Action = RebaseAction.Squash };

        var result = await repo.Rebase.RunInteractiveAsync("upstream", plan);

        Assert.True(result.Succeeded);
        Assert.Single(Subjects(fixture));
    }

    // ---------------------------------------------------------------- edit

    [Fact]
    public async Task EditStopsTheRebaseSoTheCommitCanBeAmended()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        var plan = (await repo.Rebase.GetTodoAsync("upstream")).ToList();

        plan[1] = plan[1] with { Action = RebaseAction.Edit };
        var result = await repo.Rebase.RunInteractiveAsync("upstream", plan);

        // Stopping on purpose is not a failure.
        Assert.Equal(OperationOutcome.Conflicted, result.Outcome);
        Assert.Contains("amend", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RepositoryOperation.Rebase, (await repo.History.GetStateAsync()).Operation);
    }

    [Fact]
    public async Task AnEditedRebaseCanBeContinuedToCompletion()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        var plan = (await repo.Rebase.GetTodoAsync("upstream")).ToList();

        plan[1] = plan[1] with { Action = RebaseAction.Edit };
        await repo.Rebase.RunInteractiveAsync("upstream", plan);

        var result = await repo.History.ContinueAsync();

        Assert.True(result.Succeeded);
        Assert.False((await repo.History.GetStateAsync()).IsInProgress);
        Assert.Equal(3, Subjects(fixture).Count);
    }

    [Fact]
    public async Task AmendingDuringAnEditStopIsKeptWhenTheRebaseContinues()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        var plan = (await repo.Rebase.GetTodoAsync("upstream")).ToList();

        plan[1] = plan[1] with { Action = RebaseAction.Reword };
        await repo.Rebase.RunInteractiveAsync("upstream", plan);

        // This is how "reword" is finished: amend in the commit box, then continue.
        await repo.WorkingCopy.CommitAsync("a better message", amend: true);
        var result = await repo.History.ContinueAsync();

        Assert.True(result.Succeeded);
        Assert.Contains("a better message", Subjects(fixture));
        Assert.DoesNotContain("second change", Subjects(fixture));
    }

    // ----------------------------------------------------------- conflicts

    [Fact]
    public async Task AConflictingRebaseReportsTheConflictedPaths()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "shared.txt", "original\n");
        fixture.Git("branch", "upstream");
        fixture.Commit("mine", "shared.txt", "from the branch\n");

        // Move upstream on so replaying the branch commit collides.
        fixture.Git("checkout", "--quiet", "upstream");
        fixture.Commit("theirs", "shared.txt", "from upstream\n");
        fixture.Git("checkout", "--quiet", "main");

        var repo = await OpenAsync(fixture);
        var plan = await repo.Rebase.GetTodoAsync("upstream");
        var result = await repo.Rebase.RunInteractiveAsync("upstream", plan);

        Assert.Equal(OperationOutcome.Conflicted, result.Outcome);
        Assert.Contains("shared.txt", result.ConflictedPaths);
    }

    [Fact]
    public async Task AConflictingRebaseCanBeAborted()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "shared.txt", "original\n");
        fixture.Git("branch", "upstream");
        fixture.Commit("mine", "shared.txt", "from the branch\n");
        fixture.Git("checkout", "--quiet", "upstream");
        fixture.Commit("theirs", "shared.txt", "from upstream\n");
        fixture.Git("checkout", "--quiet", "main");

        var repo = await OpenAsync(fixture);
        await repo.Rebase.RunInteractiveAsync("upstream", await repo.Rebase.GetTodoAsync("upstream"));

        var result = await repo.History.AbortAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("from the branch\n", fixture.WorkingContent("shared.txt"));
    }

    // ------------------------------------------------------- side effects

    [Fact]
    public async Task NoTemporaryFilesAreLeftBehind()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "gitgui-rebase*").Length;

        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);
        await repo.Rebase.RunInteractiveAsync("upstream", await repo.Rebase.GetTodoAsync("upstream"));

        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "gitgui-rebase*").Length);
    }

    [Fact]
    public async Task APlainReplayLeavesTheHistoryUnchanged()
    {
        using var fixture = CreateThreeCommitsOnABranch();
        var repo = await OpenAsync(fixture);

        var result = await repo.Rebase.RunInteractiveAsync(
            "upstream", await repo.Rebase.GetTodoAsync("upstream"));

        Assert.True(result.Succeeded);
        Assert.Equal(["third change", "second change", "first change"], Subjects(fixture));
    }
}
