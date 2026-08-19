using GitFork.Core;

namespace GitFork.Core.Tests;

[Trait("Category", "Integration")]
public class GitRefOperationsTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    // ------------------------------------------------------------ checkout

    [Fact]
    public async Task CheckingOutABranchMovesHead()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "feature");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.CheckoutBranchAsync("feature");

        Assert.True(result.Succeeded);
        Assert.Equal("feature", await repo.GetCurrentBranchAsync());
    }

    [Fact]
    public async Task CheckingOutANonExistentBranchFailsWithGitsOwnWording()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.CheckoutBranchAsync("nope");

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Contains("nope", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckingOutACommitDetachesHead()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.CheckoutCommitAsync(first);

        Assert.True(result.Succeeded);
        Assert.Null(await repo.GetCurrentBranchAsync());
        Assert.Equal(first, await repo.GetHeadShaAsync());
    }

    // ------------------------------------------------------ branch creation

    [Fact]
    public async Task CreatingABranchSwitchesToItByDefault()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.CreateBranchAsync("feature/new");

        Assert.True(result.Succeeded);
        Assert.Equal("feature/new", await repo.GetCurrentBranchAsync());
    }

    [Fact]
    public async Task CreatingABranchWithoutCheckoutLeavesHeadAlone()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var repo = await OpenAsync(fixture);
        await repo.Refs.CreateBranchAsync("sidelined", checkout: false);

        Assert.Equal("main", await repo.GetCurrentBranchAsync());
        Assert.Contains(await repo.GetRefsAsync(), r => r.ShortName == "sidelined");
    }

    [Fact]
    public async Task ABranchCanStartFromAnEarlierCommit()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var repo = await OpenAsync(fixture);
        await repo.Refs.CreateBranchAsync("from-first", first);

        Assert.Equal(first, await repo.GetHeadShaAsync());
    }

    [Fact]
    public async Task CreatingABranchWithAnEmptyNameIsRejectedBeforeGitIsCalled()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.CreateBranchAsync("   ");

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Contains("name is required", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatingADuplicateBranchFails()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "taken");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.CreateBranchAsync("taken");

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
    }

    // -------------------------------------------------------------- rename

    [Fact]
    public async Task RenamingABranchKeepsItsCommits()
    {
        using var fixture = TestRepository.CreateEmpty();
        var sha = fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "old-name");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.RenameBranchAsync("old-name", "new-name");

        Assert.True(result.Succeeded);
        var refs = await repo.GetRefsAsync();
        Assert.DoesNotContain(refs, r => r.ShortName == "old-name");
        Assert.Single(refs, r => r.ShortName == "new-name" && r.TargetSha == sha);
    }

    // -------------------------------------------------------------- delete

    [Fact]
    public async Task DeletingAMergedBranchSucceeds()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "merged-already");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.DeleteBranchAsync("merged-already");

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(await repo.GetRefsAsync(), r => r.ShortName == "merged-already");
    }

    [Fact]
    public async Task DeletingAnUnmergedBranchIsRefusedWithAnExplanation()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("checkout", "--quiet", "-b", "unmerged");
        fixture.Commit("work only here", "b.txt", "two\n");
        fixture.Git("checkout", "--quiet", "main");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.DeleteBranchAsync("unmerged");

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Contains("not merged anywhere", result.Message, StringComparison.Ordinal);
        Assert.Contains(await repo.GetRefsAsync(), r => r.ShortName == "unmerged");
    }

    [Fact]
    public async Task ForceDeletingAnUnmergedBranchSucceeds()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("checkout", "--quiet", "-b", "unmerged");
        fixture.Commit("work only here", "b.txt", "two\n");
        fixture.Git("checkout", "--quiet", "main");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.DeleteBranchAsync("unmerged", force: true);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(await repo.GetRefsAsync(), r => r.ShortName == "unmerged");
    }

    [Fact]
    public async Task MergedStateIsReportedSoTheUiCanWarnBeforeDeleting()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "merged-already");
        fixture.Git("checkout", "--quiet", "-b", "unmerged");
        fixture.Commit("work only here", "b.txt", "two\n");
        fixture.Git("checkout", "--quiet", "main");

        var repo = await OpenAsync(fixture);

        Assert.True(await repo.Refs.IsBranchMergedAsync("merged-already"));
        Assert.False(await repo.Refs.IsBranchMergedAsync("unmerged"));
    }

    // ---------------------------------------------------------------- tags

    [Fact]
    public async Task CreatingALightweightTagPointsAtHead()
    {
        using var fixture = TestRepository.CreateEmpty();
        var sha = fixture.Commit("first", "a.txt", "one\n");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.CreateTagAsync("v1.0.0");

        Assert.True(result.Succeeded);
        Assert.Single(await repo.GetRefsAsync(), r => r.Kind == RefKind.Tag && r.TargetSha == sha);
    }

    [Fact]
    public async Task AnAnnotatedTagCarriesItsMessage()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var repo = await OpenAsync(fixture);
        await repo.Refs.CreateTagAsync("v2.0.0", message: "the release notes");

        Assert.Contains("the release notes", fixture.Git("tag", "-n", "v2.0.0"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATagCanBeCreatedOnAnEarlierCommit()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var repo = await OpenAsync(fixture);
        await repo.Refs.CreateTagAsync("v0.9", first);

        Assert.Single(await repo.GetRefsAsync(), r => r.Kind == RefKind.Tag && r.TargetSha == first);
    }

    [Fact]
    public async Task DeletingATagRemovesIt()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("tag", "doomed");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.DeleteTagAsync("doomed");

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(await repo.GetRefsAsync(), r => r.Kind == RefKind.Tag);
    }
}
