using GitFork.Core;

namespace GitFork.Core.Tests;

/// <summary>
/// Exercises the write path against a real git binary. Hunk- and line-level staging in particular
/// depend on patch arithmetic that only real <c>git apply</c> can validate.
/// </summary>
[Trait("Category", "Integration")]
public class GitWorkingCopyTests
{
    private static async Task<(GitRepository Repo, GitWorkingCopy Wc)> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return (repo!, repo.WorkingCopy);
    }

    private static FileChange Modified(string path) => new(ChangeKind.Modified, path);

    // ------------------------------------------------------- whole files

    [Fact]
    public async Task StagingAModifiedFileMovesItFromUnstagedToStaged()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "two\n");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.StageAsync(["a.txt"]);

        var status = await repo.GetStatusAsync();
        Assert.Single(status.Staged, f => f.Path == "a.txt");
        Assert.Empty(status.Unstaged);
        Assert.Equal("two\n", fixture.IndexContent("a.txt"));
    }

    [Fact]
    public async Task UnstagingReturnsAFileToTheWorkingTreeWithoutLosingTheEdit()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "two\n");
        fixture.Git("add", "a.txt");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.UnstageAsync(["a.txt"]);

        var status = await repo.GetStatusAsync();
        Assert.Empty(status.Staged);
        Assert.Single(status.Unstaged, f => f.Path == "a.txt");
        Assert.Equal("two\n", fixture.WorkingContent("a.txt"));
    }

    [Fact]
    public async Task StagingAnUntrackedFileRecordsItAsAnAddition()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("new.txt", "brand new\n");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.StageAsync(["new.txt"]);

        var status = await repo.GetStatusAsync();
        Assert.Empty(status.Untracked);
        Assert.Single(status.Staged, f => f.Path == "new.txt" && f.Kind == ChangeKind.Added);
    }

    [Fact]
    public async Task StagingADeletionRecordsTheRemoval()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "gone.txt", "bye\n");
        fixture.DeleteFile("gone.txt");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.StageAsync(["gone.txt"]);

        var status = await repo.GetStatusAsync();
        Assert.Single(status.Staged, f => f.Path == "gone.txt" && f.Kind == ChangeKind.Deleted);
    }

    [Fact]
    public async Task UnstagingANewFileWorksBeforeTheFirstCommitExists()
    {
        // There is no HEAD to restore from yet, so this must fall back to emptying the index.
        using var fixture = TestRepository.CreateEmpty();
        fixture.WriteFile("first.txt", "content\n");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.StageAsync(["first.txt"]);
        await wc.UnstageAsync(["first.txt"]);

        var status = await repo.GetStatusAsync();
        Assert.Empty(status.Staged);
        Assert.Single(status.Untracked, f => f.Path == "first.txt");
    }

    [Fact]
    public async Task StagingAnEmptySelectionDoesNothing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "two\n");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.StageAsync([]);

        // An empty path list must not degrade into "stage everything".
        Assert.Empty((await repo.GetStatusAsync()).Staged);
    }

    // ---------------------------------------------------------- discarding

    [Fact]
    public async Task DiscardingRevertsAFileToItsStagedContent()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "original\n");
        fixture.WriteFile("a.txt", "unwanted\n");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.DiscardChangesAsync(["a.txt"]);

        Assert.Equal("original\n", fixture.WorkingContent("a.txt"));
        Assert.True((await repo.GetStatusAsync()).IsClean);
    }

    [Fact]
    public async Task DeletingAnUntrackedFileRemovesItFromDisk()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("junk.txt", "junk\n");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.DeleteUntrackedAsync(["junk.txt"]);

        Assert.False(File.Exists(Path.Combine(fixture.Path, "junk.txt")));
        Assert.True((await repo.GetStatusAsync()).IsClean);
    }

    // --------------------------------------------------------- committing

    [Fact]
    public async Task CommittingWritesTheStagedContentAndCleansTheTree()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "two\n");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.StageAsync(["a.txt"]);
        var sha = await wc.CommitAsync("second commit");

        Assert.Equal(sha, fixture.Head());
        Assert.Equal(2, fixture.CommitCount());
        Assert.Equal("two\n", fixture.HeadContent("a.txt"));
        Assert.True((await repo.GetStatusAsync()).IsClean);
    }

    [Fact]
    public async Task CommittingKeepsTheSubjectAndBodySeparate()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.WriteFile("a.txt", "one\n");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.StageAsync(["a.txt"]);
        await wc.CommitAsync("the subject\n\nthe body\nsecond line");

        var commit = (await repo.GetCommitsAsync())[0];
        var detail = await repo.GetCommitDetailAsync(commit);

        Assert.Equal("the subject", detail.Commit.Subject);
        Assert.Equal("the body\nsecond line", detail.Body);
    }

    [Fact]
    public async Task CommittingWithAnEmptyMessageIsRejected()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.WriteFile("a.txt", "one\n");

        var (_, wc) = await OpenAsync(fixture);
        await wc.StageAsync(["a.txt"]);

        await Assert.ThrowsAsync<GitException>(() => wc.CommitAsync("   "));
    }

    [Fact]
    public async Task AmendingReplacesTheHeadCommitRatherThanAddingOne()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("typo in mesage", "b.txt", "two\n");

        var (repo, wc) = await OpenAsync(fixture);
        await wc.CommitAsync("typo in message", amend: true);

        Assert.Equal(2, fixture.CommitCount());
        Assert.Equal("typo in message", (await repo.GetCommitsAsync())[0].Subject);
    }

    [Fact]
    public async Task AmendingCanAlsoFoldInNewlyStagedChanges()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "one\ntwo\n");

        var (_, wc) = await OpenAsync(fixture);
        await wc.StageAsync(["a.txt"]);
        await wc.CommitAsync("first, with more", amend: true);

        Assert.Equal(1, fixture.CommitCount());
        Assert.Equal("one\ntwo\n", fixture.HeadContent("a.txt"));
    }

    [Fact]
    public async Task TheHeadMessageIsAvailableForPrefillingAnAmend()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.WriteFile("a.txt", "one\n");
        fixture.Git("add", "-A");
        fixture.Git("commit", "--quiet", "-m", "subject here", "-m", "body here");

        var (_, wc) = await OpenAsync(fixture);

        Assert.Equal("subject here\n\nbody here", await wc.GetHeadMessageAsync());
    }

    [Fact]
    public async Task RecentMessagesAreListedNewestFirstWithoutDuplicates()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("alpha", "a.txt", "1");
        fixture.Commit("beta", "a.txt", "2");
        fixture.Commit("alpha", "a.txt", "3");

        var (_, wc) = await OpenAsync(fixture);
        var messages = await wc.GetRecentMessagesAsync();

        Assert.Equal(["alpha", "beta"], messages);
    }

    // ----------------------------------------------------- hunk staging

    /// <summary>Twenty numbered lines with edits at line 3 and line 18, so git emits two hunks.</summary>
    private static TestRepository CreateTwoHunkRepository()
    {
        var fixture = TestRepository.CreateEmpty();
        var original = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        fixture.Commit("first", "a.txt", original);

        var edited = original
            .Replace("line 3\n", "LINE THREE\n", StringComparison.Ordinal)
            .Replace("line 18\n", "LINE EIGHTEEN\n", StringComparison.Ordinal);
        fixture.WriteFile("a.txt", edited);
        return fixture;
    }

    [Fact]
    public async Task AFileWithSeparatedEditsProducesTwoHunks()
    {
        using var fixture = CreateTwoHunkRepository();
        var (_, wc) = await OpenAsync(fixture);

        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);

        Assert.NotNull(diff);
        Assert.Equal(2, diff!.Hunks.Count);
    }

    [Fact]
    public async Task StagingOnlyTheFirstHunkLeavesTheSecondUnstaged()
    {
        using var fixture = CreateTwoHunkRepository();
        var (repo, wc) = await OpenAsync(fixture);

        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);
        var patch = PatchBuilder.BuildHunkPatch(diff!, [0], PatchDirection.Stage);
        Assert.NotNull(patch);
        await wc.ApplyToIndexAsync(patch!, PatchDirection.Stage);

        var staged = fixture.IndexContent("a.txt");
        Assert.Contains("LINE THREE", staged, StringComparison.Ordinal);
        Assert.Contains("line 18", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("LINE EIGHTEEN", staged, StringComparison.Ordinal);

        // The file itself is untouched, so it appears on both sides of the index.
        var status = await repo.GetStatusAsync();
        Assert.Single(status.Staged, f => f.Path == "a.txt");
        Assert.Single(status.Unstaged, f => f.Path == "a.txt");
    }

    [Fact]
    public async Task StagingTheSecondHunkAloneUsesTheCorrectLineOffsets()
    {
        // The second hunk starts far down the file; a wrong offset would fail to apply.
        using var fixture = CreateTwoHunkRepository();
        var (_, wc) = await OpenAsync(fixture);

        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);
        var patch = PatchBuilder.BuildHunkPatch(diff!, [1], PatchDirection.Stage);
        await wc.ApplyToIndexAsync(patch!, PatchDirection.Stage);

        var staged = fixture.IndexContent("a.txt");
        Assert.Contains("line 3\n", staged, StringComparison.Ordinal);
        Assert.Contains("LINE EIGHTEEN", staged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StagingBothHunksMatchesStagingTheWholeFile()
    {
        using var fixture = CreateTwoHunkRepository();
        var (repo, wc) = await OpenAsync(fixture);

        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);
        var patch = PatchBuilder.BuildWholeFilePatch(diff!, PatchDirection.Stage);
        await wc.ApplyToIndexAsync(patch!, PatchDirection.Stage);

        Assert.Equal(fixture.WorkingContent("a.txt"), fixture.IndexContent("a.txt"));
        Assert.Empty((await repo.GetStatusAsync()).Unstaged);
    }

    [Fact]
    public async Task UnstagingASingleHunkLeavesTheOtherStaged()
    {
        using var fixture = CreateTwoHunkRepository();
        var (_, wc) = await OpenAsync(fixture);
        fixture.Git("add", "a.txt");

        var staged = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Staged);
        Assert.Equal(2, staged!.Hunks.Count);

        var patch = PatchBuilder.BuildHunkPatch(staged, [0], PatchDirection.Unstage);
        await wc.ApplyToIndexAsync(patch!, PatchDirection.Unstage);

        var index = fixture.IndexContent("a.txt");
        Assert.Contains("line 3\n", index, StringComparison.Ordinal);
        Assert.Contains("LINE EIGHTEEN", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StagingThenUnstagingEveryHunkReturnsToACleanIndex()
    {
        using var fixture = CreateTwoHunkRepository();
        var (repo, wc) = await OpenAsync(fixture);

        var unstaged = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);
        await wc.ApplyToIndexAsync(
            PatchBuilder.BuildWholeFilePatch(unstaged!, PatchDirection.Stage)!, PatchDirection.Stage);

        var staged = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Staged);
        await wc.ApplyToIndexAsync(
            PatchBuilder.BuildWholeFilePatch(staged!, PatchDirection.Unstage)!, PatchDirection.Unstage);

        var status = await repo.GetStatusAsync();
        Assert.Empty(status.Staged);
        Assert.Single(status.Unstaged, f => f.Path == "a.txt");
    }

    // ----------------------------------------------------- line staging

    [Fact]
    public async Task StagingASingleAddedLineLeavesTheOtherAdditionBehind()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "keep\n");
        fixture.WriteFile("a.txt", "keep\nfirst added\nsecond added\n");

        var (_, wc) = await OpenAsync(fixture);
        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);

        var hunk = diff!.Hunks[0];
        var firstAddition = hunk.Lines.Index().First(x => x.Item.Kind == DiffLineKind.Added).Index;

        var patch = PatchBuilder.BuildSelectionPatch(
            diff, new Dictionary<int, IReadOnlySet<int>> { [0] = new HashSet<int> { firstAddition } },
            PatchDirection.Stage);
        await wc.ApplyToIndexAsync(patch!, PatchDirection.Stage);

        Assert.Equal("keep\nfirst added\n", fixture.IndexContent("a.txt"));
        Assert.Equal("keep\nfirst added\nsecond added\n", fixture.WorkingContent("a.txt"));
    }

    [Fact]
    public async Task StagingASingleRemovedLineKeepsTheOtherRemovalInTheIndex()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "keep\ndrop one\ndrop two\n");
        fixture.WriteFile("a.txt", "keep\n");

        var (_, wc) = await OpenAsync(fixture);
        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);

        var hunk = diff!.Hunks[0];
        var firstRemoval = hunk.Lines.Index().First(x => x.Item.Kind == DiffLineKind.Removed).Index;

        var patch = PatchBuilder.BuildSelectionPatch(
            diff, new Dictionary<int, IReadOnlySet<int>> { [0] = new HashSet<int> { firstRemoval } },
            PatchDirection.Stage);
        await wc.ApplyToIndexAsync(patch!, PatchDirection.Stage);

        // Only the first removal is staged, so "drop two" survives in the index.
        Assert.Equal("keep\ndrop two\n", fixture.IndexContent("a.txt"));
    }

    [Fact]
    public async Task StagingOneSideOfAReplacementStagesAnIntermediateState()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "before\n");
        fixture.WriteFile("a.txt", "after\n");

        var (_, wc) = await OpenAsync(fixture);
        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);

        var hunk = diff!.Hunks[0];
        var addition = hunk.Lines.Index().First(x => x.Item.Kind == DiffLineKind.Added).Index;

        // Staging only the addition leaves both lines in the index: the removal is not applied.
        var patch = PatchBuilder.BuildSelectionPatch(
            diff, new Dictionary<int, IReadOnlySet<int>> { [0] = new HashSet<int> { addition } },
            PatchDirection.Stage);
        await wc.ApplyToIndexAsync(patch!, PatchDirection.Stage);

        Assert.Equal("before\nafter\n", fixture.IndexContent("a.txt"));
    }

    [Fact]
    public async Task UnstagingASingleLineLeavesTheRestStaged()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "keep\n");
        fixture.WriteFile("a.txt", "keep\nfirst added\nsecond added\n");
        fixture.Git("add", "a.txt");

        var (_, wc) = await OpenAsync(fixture);
        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Staged);

        var hunk = diff!.Hunks[0];
        var lastAddition = hunk.Lines.Index().Last(x => x.Item.Kind == DiffLineKind.Added).Index;

        var patch = PatchBuilder.BuildSelectionPatch(
            diff, new Dictionary<int, IReadOnlySet<int>> { [0] = new HashSet<int> { lastAddition } },
            PatchDirection.Unstage);
        await wc.ApplyToIndexAsync(patch!, PatchDirection.Unstage);

        Assert.Equal("keep\nfirst added\n", fixture.IndexContent("a.txt"));
    }

    [Fact]
    public async Task StagingSelectedLinesAcrossBothHunksAppliesCleanly()
    {
        // Two hunks in one patch: the second hunk's offsets must account for the first.
        using var fixture = CreateTwoHunkRepository();
        var (_, wc) = await OpenAsync(fixture);

        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);
        var selection = new Dictionary<int, IReadOnlySet<int>>();
        for (var h = 0; h < diff!.Hunks.Count; h++)
        {
            var additions = diff.Hunks[h].Lines.Index()
                .Where(x => x.Item.Kind == DiffLineKind.Added)
                .Select(x => x.Index)
                .ToHashSet();
            selection[h] = additions;
        }

        var patch = PatchBuilder.BuildSelectionPatch(diff, selection, PatchDirection.Stage);
        await wc.ApplyToIndexAsync(patch!, PatchDirection.Stage);

        var index = fixture.IndexContent("a.txt");
        // Additions staged without their paired removals, so both old and new lines are present.
        Assert.Contains("LINE THREE", index, StringComparison.Ordinal);
        Assert.Contains("line 3", index, StringComparison.Ordinal);
        Assert.Contains("LINE EIGHTEEN", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWithoutATrailingNewlineStagesCorrectly()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\ntwo");
        fixture.WriteFile("a.txt", "one\nTWO");

        var (_, wc) = await OpenAsync(fixture);
        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);
        var patch = PatchBuilder.BuildWholeFilePatch(diff!, PatchDirection.Stage);
        await wc.ApplyToIndexAsync(patch!, PatchDirection.Stage);

        Assert.Equal("one\nTWO", fixture.IndexContent("a.txt"));
    }

    [Fact]
    public async Task AnEmptySelectionProducesNoPatch()
    {
        using var fixture = CreateTwoHunkRepository();
        var (_, wc) = await OpenAsync(fixture);

        var diff = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);

        Assert.Null(PatchBuilder.BuildHunkPatch(diff!, [], PatchDirection.Stage));
        Assert.Null(PatchBuilder.BuildSelectionPatch(
            diff!, new Dictionary<int, IReadOnlySet<int>> { [0] = new HashSet<int>() }, PatchDirection.Stage));
    }

    [Fact]
    public async Task APatchThatCannotApplyReportsAGitException()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var (_, wc) = await OpenAsync(fixture);
        var bogus = "diff --git a/missing.txt b/missing.txt\n" +
                    "--- a/missing.txt\n+++ b/missing.txt\n" +
                    "@@ -1,1 +1,1 @@\n-nope\n+also nope\n";

        var error = await Assert.ThrowsAsync<GitException>(
            () => wc.ApplyToIndexAsync(bogus, PatchDirection.Stage));

        Assert.Contains("git apply", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------- untracked diffs

    [Fact]
    public async Task AnUntrackedFileIsDiffedAgainstNothing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("new.txt", "alpha\nbeta\n");

        var (_, wc) = await OpenAsync(fixture);
        var diff = await wc.GetFileDiffAsync(new FileChange(ChangeKind.Added, "new.txt"),
            DiffSide.Unstaged, isUntracked: true);

        Assert.NotNull(diff);
        var lines = diff!.Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Equal(2, lines.Count(l => l.Kind == DiffLineKind.Added));
        Assert.DoesNotContain(lines, l => l.Kind == DiffLineKind.Removed);
    }

    [Fact]
    public async Task AStagedDiffReadsFromTheIndexNotTheWorkingTree()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "staged\n");
        fixture.Git("add", "a.txt");
        fixture.WriteFile("a.txt", "further edit\n");

        var (_, wc) = await OpenAsync(fixture);
        var staged = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Staged);
        var unstaged = await wc.GetFileDiffAsync(Modified("a.txt"), DiffSide.Unstaged);

        Assert.Contains(staged!.Hunks.SelectMany(h => h.Lines),
            l => l.Kind == DiffLineKind.Added && l.Text == "staged");
        Assert.Contains(unstaged!.Hunks.SelectMany(h => h.Lines),
            l => l.Kind == DiffLineKind.Added && l.Text == "further edit");
    }
}
