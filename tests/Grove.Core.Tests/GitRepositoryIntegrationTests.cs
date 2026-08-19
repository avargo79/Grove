using System.Globalization;
using Grove.Core;
using Grove.Core.Graph;

namespace Grove.Core.Tests;

/// <summary>End-to-end coverage against a real git binary and a real repository on disk.</summary>
[Trait("Category", "Integration")]
public class GitRepositoryIntegrationTests
{
    // ------------------------------------------------------------ discovery

    [Fact]
    public async Task OpenReturnsNullOutsideAnyRepository()
    {
        var path = Path.Combine(Path.GetTempPath(), "grove-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Assert.Null(await GitRepository.OpenAsync(path));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task OpenReturnsNullForAPathThatDoesNotExist()
    {
        Assert.Null(await GitRepository.OpenAsync("/definitely/not/a/real/path/anywhere"));
    }

    [Fact]
    public async Task OpenFromASubdirectoryFindsTheWorkTreeRoot()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("initial", "src/deep/file.txt", "hello");

        var repo = await GitRepository.OpenAsync(Path.Combine(fixture.Path, "src", "deep"));

        Assert.NotNull(repo);
        // macOS temp paths are symlinked through /private, so compare resolved paths.
        Assert.Equal(
            Path.GetFullPath(fixture.Path).TrimEnd('/'),
            Path.GetFullPath(repo!.RootPath).Replace("/private", string.Empty).TrimEnd('/'));
    }

    // -------------------------------------------------------------- commits

    [Fact]
    public async Task AnEmptyRepositoryReportsNoCommitsRatherThanFailing()
    {
        using var fixture = TestRepository.CreateEmpty();
        var repo = await GitRepository.OpenAsync(fixture.Path);

        Assert.Empty(await repo!.GetCommitsAsync());
    }

    [Fact]
    public async Task CommitsAreReturnedNewestFirstWithParentLinks()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "1");
        var second = fixture.Commit("second", "a.txt", "2");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var commits = await repo!.GetCommitsAsync();

        Assert.Equal(2, commits.Count);
        Assert.Equal(second, commits[0].Sha);
        Assert.Equal("second", commits[0].Subject);
        Assert.Equal([first], commits[0].ParentShas);
        Assert.Empty(commits[1].ParentShas);
    }

    [Fact]
    public async Task CommitCarriesAuthorIdentityAndDate()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var commit = (await repo!.GetCommitsAsync())[0];

        Assert.Equal("Test Author", commit.AuthorName);
        Assert.Equal("test@example.com", commit.AuthorEmail);
        Assert.Equal(2024, commit.AuthorDate.Year);
    }

    [Fact]
    public async Task SubjectsContainingSeparatorLikeCharactersSurviveParsing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("fix: handle a|b, c\\d and \"quotes\"", "a.txt", "1");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var commit = (await repo!.GetCommitsAsync())[0];

        Assert.Equal("fix: handle a|b, c\\d and \"quotes\"", commit.Subject);
    }

    [Fact]
    public async Task MaxCountLimitsTheNumberOfCommitsReturned()
    {
        using var fixture = TestRepository.CreateEmpty();
        for (var i = 0; i < 5; i++)
            fixture.Commit($"commit {i}", "a.txt", i.ToString(CultureInfo.InvariantCulture));

        var repo = await GitRepository.OpenAsync(fixture.Path);

        Assert.Equal(3, (await repo!.GetCommitsAsync(maxCount: 3)).Count);
    }

    [Fact]
    public async Task CommitsFromEveryBranchAreIncludedWhenAllRefsIsSet()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "a.txt", "1");
        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("on feature", "b.txt", "2");
        fixture.Git("checkout", "--quiet", "main");

        var repo = await GitRepository.OpenAsync(fixture.Path);

        Assert.Contains(await repo!.GetCommitsAsync(allRefs: true), c => c.Subject == "on feature");
        Assert.DoesNotContain(await repo.GetCommitsAsync(allRefs: false), c => c.Subject == "on feature");
    }

    [Fact]
    public async Task HeadCommitIsDecoratedWithHeadAndItsBranch()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var commit = (await repo!.GetCommitsAsync())[0];

        Assert.Contains("HEAD", commit.RefNames);
        Assert.Contains("main", commit.RefNames);
    }

    [Fact]
    public async Task TagsAppearInTheDecorationWithTheirTagPrefix()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");
        fixture.Git("tag", "v1.0.0");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var commit = (await repo!.GetCommitsAsync())[0];

        Assert.Contains("tag: v1.0.0", commit.RefNames);
    }

    // ----------------------------------------------------------- merge graph

    [Fact]
    public async Task MergeCommitReportsBothParents()
    {
        using var fixture = CreateMergedHistory();
        var repo = await GitRepository.OpenAsync(fixture.Path);
        var commits = await repo!.GetCommitsAsync();

        var merge = Assert.Single(commits, c => c.IsMerge);
        Assert.Equal(2, merge.ParentShas.Count);
    }

    [Fact]
    public async Task RealMergedHistoryLaysOutOnTwoLanes()
    {
        using var fixture = CreateMergedHistory();
        var repo = await GitRepository.OpenAsync(fixture.Path);
        var commits = await repo!.GetCommitsAsync();

        var rows = CommitGraphBuilder.Build(commits);

        Assert.Equal(commits.Count, rows.Count);
        Assert.Equal(2, rows.Max(r => r.LaneCount));
        Assert.Single(rows, r => r.IsMerge);
    }

    // ----------------------------------------------------------------- refs

    [Fact]
    public async Task LocalBranchesAreListedWithHeadFlagged()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");
        fixture.Git("branch", "feature");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var refs = await repo!.GetRefsAsync();
        var locals = refs.Where(r => r.Kind == RefKind.LocalBranch).ToList();

        Assert.Equal(2, locals.Count);
        Assert.Single(locals, r => r.IsHead && r.ShortName == "main");
    }

    [Fact]
    public async Task TagsAreClassifiedSeparatelyFromBranches()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");
        fixture.Git("tag", "v1.0.0");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var refs = await repo!.GetRefsAsync();

        var tag = Assert.Single(refs, r => r.Kind == RefKind.Tag);
        Assert.Equal("v1.0.0", tag.ShortName);
    }

    [Fact]
    public async Task StashesAreClassifiedAsStashRefs()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");
        fixture.WriteFile("a.txt", "work in progress");
        fixture.Git("stash", "push", "--quiet", "-m", "wip");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var refs = await repo!.GetRefsAsync();

        Assert.Single(refs, r => r.Kind == RefKind.Stash);
    }

    [Fact]
    public async Task CurrentBranchIsReportedAndIsNullWhenDetached()
    {
        using var fixture = TestRepository.CreateEmpty();
        var sha = fixture.Commit("first", "a.txt", "1");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.Equal("main", await repo!.GetCurrentBranchAsync());

        fixture.Git("checkout", "--quiet", "--detach", sha);
        Assert.Null(await repo.GetCurrentBranchAsync());
        Assert.Equal(sha, await repo.GetHeadShaAsync());
    }

    // --------------------------------------------------------------- status

    [Fact]
    public async Task ACleanWorkingTreeReportsNoChanges()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var status = await repo!.GetStatusAsync();

        Assert.True(status.IsClean);
        Assert.Equal("main", status.Branch);
    }

    [Fact]
    public async Task StagedAndUnstagedAndUntrackedChangesAreSeparated()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "tracked.txt", "1");

        fixture.WriteFile("staged.txt", "new file");
        fixture.Git("add", "staged.txt");
        fixture.WriteFile("tracked.txt", "modified");
        fixture.WriteFile("untracked.txt", "loose");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var status = await repo!.GetStatusAsync();

        Assert.Single(status.Staged, f => f.Path == "staged.txt" && f.Kind == ChangeKind.Added);
        Assert.Single(status.Unstaged, f => f.Path == "tracked.txt" && f.Kind == ChangeKind.Modified);
        Assert.Single(status.Untracked, f => f.Path == "untracked.txt");
        Assert.Equal(3, status.TotalChanges);
    }

    [Fact]
    public async Task AStagedRenameKeepsBothPaths()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "old.txt", new string('x', 200));
        fixture.Git("mv", "old.txt", "new.txt");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var status = await repo!.GetStatusAsync();

        var rename = Assert.Single(status.Staged, f => f.Kind == ChangeKind.Renamed);
        Assert.Equal("new.txt", rename.Path);
        Assert.Equal("old.txt", rename.OldPath);
    }

    [Fact]
    public async Task DeletedFilesAreReportedAsUnstagedDeletions()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "gone.txt", "1");
        fixture.DeleteFile("gone.txt");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var status = await repo!.GetStatusAsync();

        Assert.Single(status.Unstaged, f => f.Path == "gone.txt" && f.Kind == ChangeKind.Deleted);
    }

    [Fact]
    public async Task UntrackedDirectoriesAreExpandedIntoIndividualFiles()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");
        fixture.WriteFile("newdir/one.txt", "1");
        fixture.WriteFile("newdir/two.txt", "2");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var status = await repo!.GetStatusAsync();

        Assert.Equal(2, status.Untracked.Count);
        Assert.Single(status.Untracked, f => f.Path == "newdir/one.txt");
        Assert.Single(status.Untracked, f => f.Path == "newdir/two.txt");
    }

    [Fact]
    public async Task PathsWithSpacesSurviveStatusParsing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");
        fixture.WriteFile("my folder/my file.txt", "content");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var status = await repo!.GetStatusAsync();

        Assert.Single(status.Untracked, f => f.Path == "my folder/my file.txt");
    }

    // --------------------------------------------------------- commit detail

    [Fact]
    public async Task CommitDetailSplitsTheBodyFromTheSubject()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.WriteFile("a.txt", "1");
        fixture.Git("add", "-A");
        fixture.Git("commit", "--quiet", "-m", "the subject", "-m", "the body\nsecond line");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var commit = (await repo!.GetCommitsAsync())[0];
        var detail = await repo.GetCommitDetailAsync(commit);

        Assert.Equal("the subject", detail.Commit.Subject);
        Assert.Equal("the body\nsecond line", detail.Body);
    }

    [Fact]
    public async Task CommitDetailOfASingleLineMessageHasAnEmptyBody()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("just a subject", "a.txt", "1");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var detail = await repo!.GetCommitDetailAsync((await repo.GetCommitsAsync())[0]);

        Assert.Equal(string.Empty, detail.Body);
    }

    [Fact]
    public async Task TheRootCommitListsItsFilesRatherThanNothing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("root", "first.txt", "content");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var detail = await repo!.GetCommitDetailAsync((await repo.GetCommitsAsync())[0]);

        var file = Assert.Single(detail.Files);
        Assert.Equal("first.txt", file.Path);
        Assert.Equal(ChangeKind.Added, file.Kind);
    }

    [Fact]
    public async Task CommitDetailReportsAddedModifiedAndDeletedFiles()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.WriteFile("keep.txt", "1");
        fixture.WriteFile("remove.txt", "1");
        fixture.CommitAll("first");

        fixture.WriteFile("keep.txt", "2");
        fixture.WriteFile("add.txt", "new");
        fixture.DeleteFile("remove.txt");
        fixture.CommitAll("second");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var detail = await repo!.GetCommitDetailAsync((await repo.GetCommitsAsync())[0]);

        Assert.Equal(3, detail.Files.Count);
        Assert.Single(detail.Files, f => f.Path == "add.txt" && f.Kind == ChangeKind.Added);
        Assert.Single(detail.Files, f => f.Path == "keep.txt" && f.Kind == ChangeKind.Modified);
        Assert.Single(detail.Files, f => f.Path == "remove.txt" && f.Kind == ChangeKind.Deleted);
    }

    [Fact]
    public async Task CommitDetailDetectsARename()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "old.txt", new string('x', 200));
        fixture.Git("mv", "old.txt", "renamed.txt");
        fixture.CommitAll("rename it");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var detail = await repo!.GetCommitDetailAsync((await repo.GetCommitsAsync())[0]);

        var file = Assert.Single(detail.Files);
        Assert.Equal(ChangeKind.Renamed, file.Kind);
        Assert.Equal("old.txt", file.OldPath);
        Assert.Equal("renamed.txt", file.Path);
    }

    // ----------------------------------------------------------------- diff

    [Fact]
    public async Task FileDiffReportsTheAddedAndRemovedLines()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "line one\nline two\nline three\n");
        fixture.Commit("second", "a.txt", "line one\nline TWO\nline three\n");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var detail = await repo!.GetCommitDetailAsync((await repo.GetCommitsAsync())[0]);
        var diff = await repo.GetCommitFileDiffAsync(detail.Commit.Sha, detail.Files[0]);

        Assert.Single(diff, l => l.Kind == DiffLineKind.Added && l.Text == "line TWO");
        Assert.Single(diff, l => l.Kind == DiffLineKind.Removed && l.Text == "line two");
        Assert.Contains(diff, l => l.Kind == DiffLineKind.HunkHeader);
    }

    [Fact]
    public async Task FileDiffNumbersLinesAgainstTheRealFile()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n");
        fixture.Commit("second", "a.txt",
            string.Join('\n', Enumerable.Range(1, 20).Select(i => i == 10 ? "CHANGED" : $"line {i}")) + "\n");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var detail = await repo!.GetCommitDetailAsync((await repo.GetCommitsAsync())[0]);
        var diff = await repo.GetCommitFileDiffAsync(detail.Commit.Sha, detail.Files[0]);

        var added = Assert.Single(diff, l => l.Kind == DiffLineKind.Added);
        Assert.Equal(10, added.NewLineNumber);
        Assert.Equal(10, Assert.Single(diff, l => l.Kind == DiffLineKind.Removed).OldLineNumber);
    }

    [Fact]
    public async Task DiffForANewFileIsAllAdditions()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1\n");
        fixture.Commit("second", "brand-new.txt", "alpha\nbeta\n");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var detail = await repo!.GetCommitDetailAsync((await repo.GetCommitsAsync())[0]);
        var file = Assert.Single(detail.Files, f => f.Path == "brand-new.txt");
        var diff = await repo.GetCommitFileDiffAsync(detail.Commit.Sha, file);

        Assert.Equal(2, diff.Count(l => l.Kind == DiffLineKind.Added));
        Assert.DoesNotContain(diff, l => l.Kind == DiffLineKind.Removed);
    }

    [Fact]
    public async Task DiffOfARenamedFileResolvesBothSides()
    {
        using var fixture = TestRepository.CreateEmpty();
        var body = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));
        fixture.Commit("first", "old.txt", body + "\n");
        fixture.Git("mv", "old.txt", "new.txt");
        fixture.WriteFile("new.txt", body.Replace("line 5", "LINE FIVE") + "\n");
        fixture.CommitAll("rename and edit");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var detail = await repo!.GetCommitDetailAsync((await repo.GetCommitsAsync())[0]);
        var diff = await repo.GetCommitFileDiffAsync(detail.Commit.Sha, detail.Files[0]);

        Assert.Single(diff, l => l.Kind == DiffLineKind.Added && l.Text == "LINE FIVE");
    }

    [Fact]
    public async Task DiffForABinaryFileReportsAHeaderInsteadOfLines()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1\n");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Path, "blob.bin"), [0x00, 0x01, 0x02, 0xFF, 0x00]);
        fixture.CommitAll("add binary");

        var repo = await GitRepository.OpenAsync(fixture.Path);
        var detail = await repo!.GetCommitDetailAsync((await repo.GetCommitsAsync())[0]);
        var diff = await repo.GetCommitFileDiffAsync(detail.Commit.Sha, detail.Files[0]);

        Assert.All(diff, l => Assert.Equal(DiffLineKind.Header, l.Kind));
        Assert.Contains(diff, l => l.Text.Contains("Binary files", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------- helpers

    /// <summary>main and feature diverge from a shared base, then feature is merged back.</summary>
    private static TestRepository CreateMergedHistory()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "a.txt", "base");
        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("on feature", "feature.txt", "feature work");
        fixture.Git("checkout", "--quiet", "main");
        fixture.Commit("on main", "main.txt", "main work");
        fixture.Git("merge", "--quiet", "--no-ff", "feature", "-m", "merge feature");
        return fixture;
    }
}
