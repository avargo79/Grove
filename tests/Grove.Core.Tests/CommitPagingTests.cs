using Grove.Core;

namespace Grove.Core.Tests;

/// <summary>Filtering and paging against a real repository.</summary>
[Trait("Category", "Integration")]
public class CommitPagingTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    private static TestRepository CreateHistory(int commits)
    {
        var fixture = TestRepository.CreateEmpty();
        for (var i = 1; i <= commits; i++)
            fixture.Commit($"commit {i}", "a.txt", $"content {i}\n");
        return fixture;
    }

    // -------------------------------------------------------------- paging

    [Fact]
    public async Task APageReportsWhetherThereIsMoreBehindIt()
    {
        using var fixture = CreateHistory(10);
        var repo = await OpenAsync(fixture);

        var page = await repo.GetCommitPageAsync(maxCount: 4);

        Assert.Equal(4, page.Commits.Count);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task TheLastPageReportsNoMore()
    {
        using var fixture = CreateHistory(10);
        var repo = await OpenAsync(fixture);

        var page = await repo.GetCommitPageAsync(maxCount: 20);

        Assert.Equal(10, page.Commits.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task AnExactlyFullPageDoesNotClaimThereIsMore()
    {
        using var fixture = CreateHistory(10);
        var repo = await OpenAsync(fixture);

        // The probe commit is what makes this answerable without a second query.
        var page = await repo.GetCommitPageAsync(maxCount: 10);

        Assert.Equal(10, page.Commits.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task SkippingContinuesWhereThePreviousPageStopped()
    {
        using var fixture = CreateHistory(10);
        var repo = await OpenAsync(fixture);

        // Newest first, so the first page is commits 10 down to 7.
        var first = await repo.GetCommitPageAsync(maxCount: 4);
        var second = await repo.GetCommitPageAsync(maxCount: 4, skip: 4);

        Assert.Equal("commit 10", first.Commits[0].Subject);
        Assert.Equal("commit 6", second.Commits[0].Subject);
        Assert.Empty(first.Commits.Select(c => c.Sha).Intersect(second.Commits.Select(c => c.Sha)));
        Assert.Equal(8, second.LoadedCount);
    }

    [Fact]
    public async Task PagingRightToTheEndReturnsEveryCommitExactlyOnce()
    {
        using var fixture = CreateHistory(10);
        var repo = await OpenAsync(fixture);

        var seen = new List<string>();
        var skip = 0;
        while (true)
        {
            var page = await repo.GetCommitPageAsync(maxCount: 3, skip: skip);
            seen.AddRange(page.Commits.Select(c => c.Sha));
            if (!page.HasMore)
                break;
            skip += page.Commits.Count;
        }

        Assert.Equal(10, seen.Count);
        Assert.Equal(10, seen.Distinct().Count());
    }

    [Fact]
    public async Task AnEmptyRepositoryPagesToNothing()
    {
        using var fixture = TestRepository.CreateEmpty();
        var repo = await OpenAsync(fixture);

        var page = await repo.GetCommitPageAsync();

        Assert.Empty(page.Commits);
        Assert.False(page.HasMore);
    }

    // ------------------------------------------------------------ filtering

    [Fact]
    public async Task SearchingTheMessageNarrowsTheHistory()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("fix the login bug", "a.txt", "1\n");
        fixture.Commit("add a feature", "b.txt", "2\n");
        fixture.Commit("fix the logout bug", "c.txt", "3\n");

        var repo = await OpenAsync(fixture);
        var page = await repo.GetCommitPageAsync(filter: new CommitFilter { Text = "fix" });

        Assert.Equal(2, page.Commits.Count);
        Assert.All(page.Commits, c => Assert.Contains("fix", c.Subject, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MessageSearchIsCaseInsensitive()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("Fix The Thing", "a.txt", "1\n");

        var repo = await OpenAsync(fixture);
        var page = await repo.GetCommitPageAsync(filter: new CommitFilter { Text = "fix the" });

        Assert.Single(page.Commits);
    }

    [Fact]
    public async Task MessageSearchTreatsTheQueryAsLiteralText()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("fix(auth): tighten the check", "a.txt", "1\n");
        fixture.Commit("unrelated", "b.txt", "2\n");

        var repo = await OpenAsync(fixture);

        // As a regex this would fail to compile or match nothing useful.
        var page = await repo.GetCommitPageAsync(filter: new CommitFilter { Text = "fix(auth)" });

        Assert.Single(page.Commits);
    }

    [Fact]
    public async Task SearchingByAuthorNarrowsTheHistory()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("from the default author", "a.txt", "1\n");
        fixture.WriteFile("b.txt", "2\n");
        fixture.Git("add", "-A");
        fixture.Git("-c", "user.name=Grace Hopper", "-c", "user.email=grace@example.com",
            "commit", "--quiet", "-m", "from grace");

        var repo = await OpenAsync(fixture);
        var page = await repo.GetCommitPageAsync(filter: new CommitFilter { Author = "grace" });

        Assert.Single(page.Commits);
        Assert.Equal("from grace", page.Commits[0].Subject);
    }

    [Fact]
    public async Task SearchingByPathNarrowsToCommitsThatTouchedIt()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("touches a", "a.txt", "1\n");
        fixture.Commit("touches b", "b.txt", "2\n");
        fixture.Commit("touches a again", "a.txt", "3\n");

        var repo = await OpenAsync(fixture);
        var page = await repo.GetCommitPageAsync(filter: new CommitFilter { Path = "a.txt" });

        Assert.Equal(2, page.Commits.Count);
        Assert.DoesNotContain(page.Commits, c => c.Subject == "touches b");
    }

    [Fact]
    public async Task CombiningMessageAndAuthorRequiresBoth()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("fix something", "a.txt", "1\n");
        fixture.WriteFile("b.txt", "2\n");
        fixture.Git("add", "-A");
        fixture.Git("-c", "user.name=Grace Hopper", "-c", "user.email=grace@example.com",
            "commit", "--quiet", "-m", "add something");

        var repo = await OpenAsync(fixture);
        var page = await repo.GetCommitPageAsync(
            filter: new CommitFilter { Text = "fix", Author = "grace" });

        // Neither commit matches both, and git's default OR would have returned two.
        Assert.Empty(page.Commits);
    }

    [Fact]
    public async Task AFilterThatMatchesNothingReturnsAnEmptyPage()
    {
        using var fixture = CreateHistory(5);
        var repo = await OpenAsync(fixture);

        var page = await repo.GetCommitPageAsync(filter: new CommitFilter { Text = "nothing matches this" });

        Assert.Empty(page.Commits);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task FilteredResultsPageJustLikeUnfilteredOnes()
    {
        using var fixture = TestRepository.CreateEmpty();
        for (var i = 1; i <= 8; i++)
            fixture.Commit($"fix number {i}", "a.txt", $"{i}\n");
        fixture.Commit("something else", "b.txt", "x\n");

        var repo = await OpenAsync(fixture);
        var filter = new CommitFilter { Text = "fix" };

        var first = await repo.GetCommitPageAsync(maxCount: 5, filter: filter);
        var second = await repo.GetCommitPageAsync(maxCount: 5, skip: 5, filter: filter);

        Assert.Equal(5, first.Commits.Count);
        Assert.True(first.HasMore);
        Assert.Equal(3, second.Commits.Count);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task DateBoundsNarrowTheHistory()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("the only commit", "a.txt", "1\n");

        var repo = await OpenAsync(fixture);

        // The fixture pins every commit to 1 Jan 2024.
        var before = await repo.GetCommitPageAsync(
            filter: new CommitFilter { Until = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        var after = await repo.GetCommitPageAsync(
            filter: new CommitFilter { Since = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        Assert.Empty(before.Commits);
        Assert.Single(after.Commits);
    }

    [Fact]
    public async Task StashCommitsStayOutOfFilteredResultsToo()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("the commit", "a.txt", "1\n");
        fixture.WriteFile("a.txt", "work\n");
        fixture.Git("stash", "push", "--quiet", "-m", "the commit in progress");

        var repo = await OpenAsync(fixture);
        var page = await repo.GetCommitPageAsync(filter: new CommitFilter { Text = "the commit" });

        Assert.Single(page.Commits);
    }
}
