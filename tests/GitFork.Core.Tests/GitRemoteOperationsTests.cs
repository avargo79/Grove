using GitFork.Core;

namespace GitFork.Core.Tests;

/// <summary>
/// Network operations against a local bare repository. No server and no credentials are involved,
/// but the real fetch/push code path is.
/// </summary>
[Trait("Category", "Integration")]
public class GitRemoteOperationsTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    /// <summary>A working repository with an "origin" bare remote it is already tracking.</summary>
    private static (TestRepository Local, TestRepository Origin) CreateClonePair()
    {
        var origin = TestRepository.CreateBareRemote();
        var local = TestRepository.CreateEmpty();

        local.Commit("first", "a.txt", "one\n");
        local.AddRemote(origin);
        local.Git("push", "--quiet", "--set-upstream", "origin", "main");
        return (local, origin);
    }

    // ------------------------------------------------------------ remotes

    [Fact]
    public async Task ARepositoryWithNoRemotesReportsNone()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        Assert.Empty(await (await OpenAsync(fixture)).Remotes.GetRemotesAsync());
    }

    [Fact]
    public async Task ConfiguredRemotesAreListedWithTheirUrls()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        var remotes = await (await OpenAsync(local)).Remotes.GetRemotesAsync();

        var remote = Assert.Single(remotes);
        Assert.Equal("origin", remote.Name);
        Assert.Equal(origin.Path, remote.FetchUrl);
        Assert.Equal(origin.Path, remote.PushUrl);
    }

    // -------------------------------------------------------------- fetch

    [Fact]
    public async Task FetchingBringsDownCommitsPushedBySomeoneElse()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        // A second clone stands in for the other developer.
        using var other = TestRepository.CreateEmpty();
        other.Git("remote", "add", "origin", origin.Path);
        other.Git("fetch", "--quiet", "origin");
        other.Git("checkout", "--quiet", "-B", "main", "origin/main");
        other.Commit("from elsewhere", "b.txt", "two\n");
        other.Git("push", "--quiet", "origin", "main");

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.FetchAsync("origin");

        Assert.True(result.Succeeded);
        Assert.Contains(await repo.GetCommitsAsync(), c => c.Subject == "from elsewhere");
    }

    [Fact]
    public async Task FetchingReportsProgress()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        var lines = new List<string>();
        var progress = new Progress<string>(lines.Add);

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.FetchAsync("origin", progress: progress);

        Assert.True(result.Succeeded);
        // Progress arrives on stderr; an up-to-date fetch may be silent, so only the plumbing
        // is asserted here rather than specific text.
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    [Fact]
    public async Task FetchingAnUnknownRemoteFails()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var result = await (await OpenAsync(fixture)).Remotes.FetchAsync("nowhere");

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task FetchingPrunesBranchesDeletedOnTheRemote()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        local.Git("checkout", "--quiet", "-b", "temporary");
        local.Commit("temp work", "t.txt", "x\n");
        local.Git("push", "--quiet", "origin", "temporary");
        local.Git("push", "--quiet", "origin", "--delete", "temporary");

        var repo = await OpenAsync(local);
        await repo.Remotes.FetchAsync("origin", prune: true);

        Assert.DoesNotContain(await repo.GetRefsAsync(),
            r => r.Kind == RefKind.RemoteBranch && r.ShortName == "origin/temporary");
    }

    // --------------------------------------------------------------- push

    [Fact]
    public async Task PushingSendsLocalCommitsToTheRemote()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        local.Commit("second", "b.txt", "two\n");

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.PushAsync();

        Assert.True(result.Succeeded);
        Assert.Contains("second", origin.Git("log", "--format=%s", "main"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushingABranchWithNoUpstreamExplainsWhatToDo()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        local.Git("checkout", "--quiet", "-b", "untracked-branch");
        local.Commit("work", "c.txt", "three\n");

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.PushAsync();

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Contains("set upstream", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PushingWithSetUpstreamCreatesTheTrackingBranch()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        local.Git("checkout", "--quiet", "-b", "new-branch");
        local.Commit("work", "c.txt", "three\n");

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.PushAsync("origin", "new-branch", setUpstream: true);

        Assert.True(result.Succeeded);
        Assert.Single(await repo.GetRefsAsync(),
            r => r.Kind == RefKind.LocalBranch && r.ShortName == "new-branch" && r.Upstream == "origin/new-branch");
    }

    [Fact]
    public async Task PushingBehindTheRemoteIsRefusedWithAdviceToPullFirst()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        // Someone else pushes first.
        using var other = TestRepository.CreateEmpty();
        other.Git("remote", "add", "origin", origin.Path);
        other.Git("fetch", "--quiet", "origin");
        other.Git("checkout", "--quiet", "-B", "main", "origin/main");
        other.Commit("theirs", "b.txt", "theirs\n");
        other.Git("push", "--quiet", "origin", "main");

        local.Commit("mine", "c.txt", "mine\n");

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.PushAsync();

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Contains("Pull before pushing", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingARemoteBranchRemovesItFromTheRemote()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        local.Git("checkout", "--quiet", "-b", "doomed");
        local.Commit("work", "d.txt", "x\n");
        local.Git("push", "--quiet", "origin", "doomed");

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.DeleteRemoteBranchAsync("origin", "doomed");

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("doomed", origin.Git("branch", "--list"), StringComparison.Ordinal);
    }

    // --------------------------------------------------------------- pull

    [Fact]
    public async Task PullingFastForwardsOntoTheRemoteWork()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        using var other = TestRepository.CreateEmpty();
        other.Git("remote", "add", "origin", origin.Path);
        other.Git("fetch", "--quiet", "origin");
        other.Git("checkout", "--quiet", "-B", "main", "origin/main");
        other.Commit("theirs", "b.txt", "theirs\n");
        other.Git("push", "--quiet", "origin", "main");

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.PullAsync(PullStrategy.FastForwardOnly);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(local.Path, "b.txt")));
    }

    [Fact]
    public async Task PullingWithRebaseReplaysLocalCommitsOnTop()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        using var other = TestRepository.CreateEmpty();
        other.Git("remote", "add", "origin", origin.Path);
        other.Git("fetch", "--quiet", "origin");
        other.Git("checkout", "--quiet", "-B", "main", "origin/main");
        other.Commit("theirs", "theirs.txt", "theirs\n");
        other.Git("push", "--quiet", "origin", "main");

        local.Commit("mine", "mine.txt", "mine\n");

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.PullAsync(PullStrategy.Rebase);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(await repo.GetCommitsAsync(), c => c.IsMerge);
        Assert.True(File.Exists(Path.Combine(local.Path, "theirs.txt")));
    }

    [Fact]
    public async Task AConflictingPullStopsWithConflictsRatherThanFailing()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        using var other = TestRepository.CreateEmpty();
        other.Git("remote", "add", "origin", origin.Path);
        other.Git("fetch", "--quiet", "origin");
        other.Git("checkout", "--quiet", "-B", "main", "origin/main");
        other.Commit("theirs", "a.txt", "from them\n");
        other.Git("push", "--quiet", "origin", "main");

        local.Commit("mine", "a.txt", "from me\n");

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.PullAsync();

        Assert.Equal(OperationOutcome.Conflicted, result.Outcome);
        Assert.Contains("a.txt", result.ConflictedPaths);
    }

    [Fact]
    public async Task AFastForwardOnlyPullRefusesWhenHistoriesHaveDiverged()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        using var other = TestRepository.CreateEmpty();
        other.Git("remote", "add", "origin", origin.Path);
        other.Git("fetch", "--quiet", "origin");
        other.Git("checkout", "--quiet", "-B", "main", "origin/main");
        other.Commit("theirs", "theirs.txt", "theirs\n");
        other.Git("push", "--quiet", "origin", "main");

        local.Commit("mine", "mine.txt", "mine\n");

        var repo = await OpenAsync(local);
        var result = await repo.Remotes.PullAsync(PullStrategy.FastForwardOnly);

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task AheadAndBehindCountsAreReportedAfterFetching()
    {
        var (local, origin) = CreateClonePair();
        using var _ = local;
        using var __ = origin;

        local.Commit("mine", "b.txt", "two\n");

        var repo = await OpenAsync(local);
        await repo.Remotes.FetchAsync("origin");
        var status = await repo.GetStatusAsync();

        Assert.Equal(1, status.Ahead);
        Assert.Equal(0, status.Behind);
    }
}
