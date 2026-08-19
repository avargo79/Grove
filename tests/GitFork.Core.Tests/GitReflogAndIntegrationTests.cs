using GitFork.Core;

namespace GitFork.Core.Tests;

[Trait("Category", "Integration")]
public class GitReflogAndIntegrationTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    // ------------------------------------------------------------- reflog

    [Fact]
    public async Task TheReflogRecordsEveryCommit()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var entries = await (await OpenAsync(fixture)).Reflog.GetEntriesAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal("HEAD@{0}", entries[0].Selector);
        Assert.Equal("commit", entries[0].Action);
        Assert.Equal("second", entries[0].Subject);
    }

    [Fact]
    public async Task ReflogEntriesCarryTheirFullShaAndDate()
    {
        using var fixture = TestRepository.CreateEmpty();
        var sha = fixture.Commit("only", "a.txt", "one\n");

        var entry = Assert.Single(await (await OpenAsync(fixture)).Reflog.GetEntriesAsync());

        Assert.Equal(sha, entry.Sha);
        Assert.Equal(2024, entry.Date.Year);
    }

    [Fact]
    public async Task AResetIsRecordedAsADestructiveAction()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");
        fixture.Git("reset", "--hard", "HEAD~1");

        var entries = await (await OpenAsync(fixture)).Reflog.GetEntriesAsync();

        Assert.Equal("reset", entries[0].Action);
        Assert.True(entries[0].IsPotentiallyDestructive);
        Assert.False(entries[^1].IsPotentiallyDestructive);
    }

    [Fact]
    public async Task ACommitOrphanedByAResetIsStillFindableInTheReflog()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        var lost = fixture.Commit("work I did not mean to lose", "a.txt", "two\n");
        fixture.Git("reset", "--hard", "HEAD~1");

        var repo = await OpenAsync(fixture);
        var unreachable = await repo.Reflog.GetUnreachableEntriesAsync();

        // This is the whole point of the reflog browser.
        Assert.Contains(unreachable, e => e.Sha == lost);
    }

    [Fact]
    public async Task NothingIsUnreachableInAHealthyRepository()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        Assert.Empty(await (await OpenAsync(fixture)).Reflog.GetUnreachableEntriesAsync());
    }

    [Fact]
    public async Task AnOrphanedCommitCanBeRecoveredOntoABranch()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        var lost = fixture.Commit("lost work", "a.txt", "two\n");
        fixture.Git("reset", "--hard", "HEAD~1");

        var repo = await OpenAsync(fixture);
        var result = await repo.Refs.CreateBranchAsync("recovered", lost);

        Assert.True(result.Succeeded);
        Assert.Equal("two\n", fixture.WorkingContent("a.txt"));
        Assert.Empty(await repo.Reflog.GetUnreachableEntriesAsync());
    }

    [Theory]
    [InlineData("commit: fix the thing", "commit", "fix the thing")]
    [InlineData("reset: moving to HEAD~1", "reset", "moving to HEAD~1")]
    [InlineData("checkout: moving from main to feature", "checkout", "moving from main to feature")]
    [InlineData("rebase (finish): returning to refs/heads/main", "rebase (finish)", "returning to refs/heads/main")]
    [InlineData("no colon here", "no colon here", "")]
    public void TheReflogSubjectSplitsIntoActionAndDetail(string subject, string action, string detail)
    {
        var (parsedAction, parsedDetail) = GitReflogOperations.SplitAction(subject);

        Assert.Equal(action, parsedAction);
        Assert.Equal(detail, parsedDetail);
    }

    // --------------------------------------------------------- signatures

    [Theory]
    [InlineData("G", SignatureStatus.Good)]
    [InlineData("U", SignatureStatus.Untrusted)]
    [InlineData("B", SignatureStatus.Bad)]
    [InlineData("E", SignatureStatus.Unknown)]
    [InlineData("N", SignatureStatus.None)]
    [InlineData("", SignatureStatus.None)]
    public void SignatureCodesAreMapped(string code, SignatureStatus expected)
    {
        Assert.Equal(expected, GitIntegrationOperations.ParseSignatureCode(code));
    }

    [Fact]
    public async Task UnsignedCommitsProduceNoSignatureEntries()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        // The fixture disables signing, so nothing should be reported as signed.
        Assert.Empty(await (await OpenAsync(fixture)).Integrations.GetSignatureStatusAsync());
    }

    // --------------------------------------------------------- submodules

    [Fact]
    public async Task ARepositoryWithNoSubmodulesReportsNone()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        Assert.Empty(await (await OpenAsync(fixture)).Integrations.GetSubmodulesAsync());
    }

    [Fact]
    public async Task ASubmoduleIsListedWithItsPathAndCommit()
    {
        using var inner = TestRepository.CreateEmpty();
        inner.Commit("inner commit", "inner.txt", "content\n");

        using var outer = TestRepository.CreateEmpty();
        outer.Commit("outer commit", "outer.txt", "content\n");
        outer.Git("-c", "protocol.file.allow=always", "submodule", "add", "--quiet", inner.Path, "vendor/lib");
        outer.CommitAll("add the submodule");

        var submodule = Assert.Single(await (await OpenAsync(outer)).Integrations.GetSubmodulesAsync());

        Assert.Equal("vendor/lib", submodule.Path);
        Assert.Equal(inner.Head(), submodule.Sha);
        Assert.Equal(SubmoduleState.UpToDate, submodule.State);
    }

    // ---------------------------------------------------------------- LFS

    [Fact]
    public async Task LfsQueriesAreQuietWhenItIsNotInstalledOrNotUsed()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        var repo = await OpenAsync(fixture);

        // Whether git-lfs exists on this machine is not something a test can assume; either way
        // these must return empty rather than throwing.
        Assert.Empty(await repo.Integrations.GetLfsFilesAsync());
        Assert.Null(await Record.ExceptionAsync(() => repo.Integrations.IsLfsEnabledAsync()));
    }

    // ------------------------------------------------------------ gitflow

    [Fact]
    public async Task TheDefaultFlowConfigUsesTheRepositorysOwnMainBranch()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var config = await (await OpenAsync(fixture)).Flow.GetConfigAsync();

        Assert.Equal("main", config.Main);
        Assert.Equal("develop", config.Develop);
        Assert.Equal("feature/", config.FeaturePrefix);
    }

    [Fact]
    public async Task FlowConfigIsReadFromTheRepositoryWhereItIsSet()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("config", "gitflow.prefix.feature", "feat/");
        fixture.Git("config", "gitflow.branch.develop", "dev");

        var config = await (await OpenAsync(fixture)).Flow.GetConfigAsync();

        Assert.Equal("feat/", config.FeaturePrefix);
        Assert.Equal("dev", config.Develop);
    }

    [Fact]
    public async Task StartingAFeatureBranchesFromDevelop()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");

        var repo = await OpenAsync(fixture);
        var result = await repo.Flow.StartAsync(FlowBranchKind.Feature, "login");

        Assert.True(result.Succeeded);
        Assert.Equal("feature/login", await repo.GetCurrentBranchAsync());
    }

    [Fact]
    public async Task StartingAHotfixBranchesFromMainInstead()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");
        fixture.Git("checkout", "--quiet", "develop");
        fixture.Commit("only on develop", "b.txt", "two\n");

        var repo = await OpenAsync(fixture);
        await repo.Flow.StartAsync(FlowBranchKind.Hotfix, "urgent");

        // Branched from main, so develop's extra commit is not present.
        Assert.Equal("hotfix/urgent", await repo.GetCurrentBranchAsync());
        Assert.False(File.Exists(Path.Combine(fixture.Path, "b.txt")));
    }

    [Fact]
    public async Task StartingWithoutTheBaseBranchSaysSoRatherThanBranchingFromHead()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var repo = await OpenAsync(fixture);
        var result = await repo.Flow.StartAsync(FlowBranchKind.Feature, "login");

        // There is no develop branch here; silently branching from HEAD would be wrong.
        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Contains("does not exist", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinishingAFeatureMergesItIntoDevelopAndDeletesIt()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");

        var repo = await OpenAsync(fixture);
        await repo.Flow.StartAsync(FlowBranchKind.Feature, "login");
        fixture.Commit("the feature work", "login.cs", "code\n");

        var result = await repo.Flow.FinishAsync(FlowBranchKind.Feature, "login");

        Assert.True(result.Succeeded);
        Assert.Equal("develop", await repo.GetCurrentBranchAsync());
        Assert.True(File.Exists(Path.Combine(fixture.Path, "login.cs")));
        Assert.DoesNotContain(await repo.GetRefsAsync(), r => r.ShortName == "feature/login");
    }

    [Fact]
    public async Task FinishingAReleaseMergesIntoBothMainAndDevelop()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");

        var repo = await OpenAsync(fixture);
        await repo.Flow.StartAsync(FlowBranchKind.Release, "1.0");
        fixture.Commit("release prep", "version.txt", "1.0\n");

        var result = await repo.Flow.FinishAsync(FlowBranchKind.Release, "1.0");

        Assert.True(result.Succeeded);
        Assert.Contains("release prep", fixture.Git("log", "--format=%s", "main"), StringComparison.Ordinal);
        Assert.Contains("release prep", fixture.Git("log", "--format=%s", "develop"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlowBranchesAreListedWithoutTheirPrefix()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");
        fixture.Git("branch", "feature/login");
        fixture.Git("branch", "feature/signup");
        fixture.Git("branch", "hotfix/urgent");

        var repo = await OpenAsync(fixture);
        var features = await repo.Flow.GetBranchesAsync(FlowBranchKind.Feature);

        Assert.Equal(["login", "signup"], features.Order());
    }

    [Fact]
    public async Task StartingWithAnEmptyNameIsRefused()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");

        var result = await (await OpenAsync(fixture)).Flow.StartAsync(FlowBranchKind.Feature, "  ");

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
    }
}
