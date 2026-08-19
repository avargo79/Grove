using GitGui.Core;

namespace GitGui.Core.Tests;

[Trait("Category", "Integration")]
public class GitStashOperationsTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    [Fact]
    public async Task ARepositoryWithNothingStashedReportsAnEmptyList()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        Assert.Empty(await (await OpenAsync(fixture)).Stashes.GetStashesAsync());
    }

    [Fact]
    public async Task StashingClearsTheWorkingTree()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "work in progress\n");

        var repo = await OpenAsync(fixture);
        var result = await repo.Stashes.PushAsync("my wip");

        Assert.True(result.Succeeded);
        Assert.True((await repo.GetStatusAsync()).IsClean);
        Assert.Equal("one\n", fixture.WorkingContent("a.txt"));
    }

    [Fact]
    public async Task StashingACleanTreeIsRefusedRatherThanSilentlyDoingNothing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var result = await (await OpenAsync(fixture)).Stashes.PushAsync("nothing here");

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Contains("nothing to stash", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStashCarriesTheMessageItWasGiven()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "work\n");

        var repo = await OpenAsync(fixture);
        await repo.Stashes.PushAsync("half-finished refactor");

        var stash = Assert.Single(await repo.Stashes.GetStashesAsync());
        Assert.Contains("half-finished refactor", stash.Message, StringComparison.Ordinal);
        Assert.Equal("stash@{0}", stash.Reference);
        Assert.Equal(0, stash.Index);
    }

    [Fact]
    public async Task AnAutomaticStashMessageHasItsWipPrefixStrippedForDisplay()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("the base commit", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "work\n");

        var repo = await OpenAsync(fixture);
        await repo.Stashes.PushAsync();

        var stash = Assert.Single(await repo.Stashes.GetStashesAsync());
        Assert.StartsWith("WIP on main", stash.Message, StringComparison.Ordinal);
        Assert.Equal("main", stash.Branch);
        Assert.DoesNotContain("WIP on", stash.DisplayMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntrackedFilesAreOnlyStashedWhenAskedFor()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "changed\n");
        fixture.WriteFile("loose.txt", "untracked\n");

        var repo = await OpenAsync(fixture);
        await repo.Stashes.PushAsync("tracked only");

        Assert.True(File.Exists(Path.Combine(fixture.Path, "loose.txt")));

        fixture.WriteFile("a.txt", "changed again\n");
        await repo.Stashes.PushAsync("with untracked", includeUntracked: true);

        Assert.False(File.Exists(Path.Combine(fixture.Path, "loose.txt")));
    }

    [Fact]
    public async Task StashesAreListedNewestFirst()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var repo = await OpenAsync(fixture);
        fixture.WriteFile("a.txt", "older\n");
        await repo.Stashes.PushAsync("older stash");
        fixture.WriteFile("a.txt", "newer\n");
        await repo.Stashes.PushAsync("newer stash");

        var stashes = await repo.Stashes.GetStashesAsync();

        Assert.Equal(2, stashes.Count);
        Assert.Contains("newer stash", stashes[0].Message, StringComparison.Ordinal);
        Assert.Contains("older stash", stashes[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyingAStashRestoresTheChangesAndKeepsIt()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "restored\n");

        var repo = await OpenAsync(fixture);
        await repo.Stashes.PushAsync("keep me");
        var result = await repo.Stashes.ApplyAsync("stash@{0}");

        Assert.True(result.Succeeded);
        Assert.Equal("restored\n", fixture.WorkingContent("a.txt"));
        Assert.Single(await repo.Stashes.GetStashesAsync());
    }

    [Fact]
    public async Task PoppingAStashRestoresTheChangesAndRemovesIt()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "restored\n");

        var repo = await OpenAsync(fixture);
        await repo.Stashes.PushAsync("pop me");
        var result = await repo.Stashes.PopAsync("stash@{0}");

        Assert.True(result.Succeeded);
        Assert.Equal("restored\n", fixture.WorkingContent("a.txt"));
        Assert.Empty(await repo.Stashes.GetStashesAsync());
    }

    [Fact]
    public async Task DroppingAStashRemovesItWithoutTouchingTheWorkingTree()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "discarded\n");

        var repo = await OpenAsync(fixture);
        await repo.Stashes.PushAsync("drop me");
        var result = await repo.Stashes.DropAsync("stash@{0}");

        Assert.True(result.Succeeded);
        Assert.Empty(await repo.Stashes.GetStashesAsync());
        Assert.Equal("one\n", fixture.WorkingContent("a.txt"));
    }

    [Fact]
    public async Task ApplyingAStashOverUncommittedEditsIsRefusedWithUsableAdvice()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "original\n");

        var repo = await OpenAsync(fixture);
        fixture.WriteFile("a.txt", "stashed version\n");
        await repo.Stashes.PushAsync("mine");

        // Git refuses up front here rather than producing markers, and its own wording
        // ("would be overwritten by merge") does not say what to do next.
        fixture.WriteFile("a.txt", "uncommitted edit\n");

        var result = await repo.Stashes.ApplyAsync("stash@{0}");

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Contains("Commit or stash them first", result.Message, StringComparison.Ordinal);
        Assert.Equal("uncommitted edit\n", fixture.WorkingContent("a.txt"));
    }

    [Fact]
    public async Task ApplyingAStashOverCommittedConflictingWorkReportsAConflict()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "original\n");

        var repo = await OpenAsync(fixture);
        fixture.WriteFile("a.txt", "stashed version\n");
        await repo.Stashes.PushAsync("mine");

        // Committed this time, so git attempts the merge and leaves markers behind.
        fixture.Commit("conflicting work", "a.txt", "committed version\n");

        var result = await repo.Stashes.ApplyAsync("stash@{0}");

        Assert.Equal(OperationOutcome.Conflicted, result.Outcome);
        Assert.Contains("a.txt", result.ConflictedPaths);
    }

    [Fact]
    public async Task AStashCanBePreviewedBeforeBeingApplied()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "before\n");
        fixture.WriteFile("a.txt", "after\n");

        var repo = await OpenAsync(fixture);
        await repo.Stashes.PushAsync("preview me");

        var files = await repo.Stashes.GetStashDiffAsync("stash@{0}");

        var file = Assert.Single(files);
        Assert.Equal("a.txt", file.Path);
        var lines = file.Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Added && l.Text == "after");
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Removed && l.Text == "before");
    }

    [Fact]
    public async Task DroppingAnUnknownStashFails()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var result = await (await OpenAsync(fixture)).Stashes.DropAsync("stash@{7}");

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task StashesAppearAmongTheRepositoryRefs()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "work\n");

        var repo = await OpenAsync(fixture);
        await repo.Stashes.PushAsync("visible in the sidebar");

        Assert.Single(await repo.GetRefsAsync(), r => r.Kind == RefKind.Stash);
    }
}
