using GitFork.Core;

namespace GitFork.Core.Tests;

[Trait("Category", "Integration")]
public class GitFileOperationsTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    // -------------------------------------------------------------- blame

    [Fact]
    public async Task BlameAttributesEveryLineToTheCommitThatWroteIt()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "line one\nline two\n");
        fixture.Commit("second", "a.txt", "line one\nline two changed\nline three\n");

        var repo = await OpenAsync(fixture);
        var blame = await repo.Files.GetBlameAsync("a.txt");

        Assert.Equal(3, blame.Count);
        Assert.Equal("first", blame[0].Summary);
        Assert.Equal("second", blame[1].Summary);
        Assert.Equal("second", blame[2].Summary);
    }

    [Fact]
    public async Task BlameCarriesLineNumbersAuthorsAndText()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("only", "a.txt", "alpha\nbeta\n");

        var repo = await OpenAsync(fixture);
        var blame = await repo.Files.GetBlameAsync("a.txt");

        Assert.Equal(1, blame[0].LineNumber);
        Assert.Equal(2, blame[1].LineNumber);
        Assert.Equal("alpha", blame[0].Text);
        Assert.Equal("beta", blame[1].Text);
        Assert.All(blame, l => Assert.Equal("Test Author", l.Author));
        Assert.All(blame, l => Assert.Equal(2024, l.Date.Year));
    }

    [Fact]
    public async Task BlameRepeatsCommitDetailsForEveryLineOfThatCommit()
    {
        // The porcelain format only spells out a commit once, so this checks the header cache.
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("shared commit", "a.txt", "one\ntwo\nthree\nfour\n");

        var repo = await OpenAsync(fixture);
        var blame = await repo.Files.GetBlameAsync("a.txt");

        Assert.Equal(4, blame.Count);
        Assert.All(blame, l => Assert.Equal("shared commit", l.Summary));
        Assert.All(blame, l => Assert.Equal("Test Author", l.Author));
        Assert.Single(blame.Select(l => l.Sha).Distinct());
    }

    [Fact]
    public async Task BlameCanBeTakenAtAnEarlierRevision()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "original\n");
        fixture.Commit("second", "a.txt", "rewritten\n");

        var repo = await OpenAsync(fixture);
        var blame = await repo.Files.GetBlameAsync("a.txt", first);

        Assert.Equal("original", Assert.Single(blame).Text);
    }

    [Fact]
    public async Task BlameOfAnUnknownPathIsEmptyRatherThanThrowing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        Assert.Empty(await (await OpenAsync(fixture)).Files.GetBlameAsync("nope.txt"));
    }

    [Fact]
    public async Task UncommittedLinesAreFlaggedAsSuch()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "committed\n");
        fixture.WriteFile("a.txt", "committed\nnot yet committed\n");

        var repo = await OpenAsync(fixture);
        var blame = await repo.Files.GetBlameAsync("a.txt");

        Assert.False(blame[0].IsUncommitted);
        Assert.True(blame[1].IsUncommitted);
    }

    // ------------------------------------------------------- file history

    [Fact]
    public async Task FileHistoryListsOnlyCommitsThatTouchedThePath()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("touches a", "a.txt", "one\n");
        fixture.Commit("touches b", "b.txt", "two\n");
        fixture.Commit("touches a again", "a.txt", "three\n");

        var repo = await OpenAsync(fixture);
        var history = await repo.Files.GetFileHistoryAsync("a.txt");

        Assert.Equal(2, history.Count);
        Assert.Equal("touches a again", history[0].Subject);
        Assert.Equal("touches a", history[1].Subject);
    }

    [Fact]
    public async Task FileHistoryFollowsAPathThroughARename()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("created under the old name", "old.txt", new string('x', 200));
        fixture.Git("mv", "old.txt", "new.txt");
        fixture.CommitAll("renamed it");

        var repo = await OpenAsync(fixture);
        var history = await repo.Files.GetFileHistoryAsync("new.txt");

        // Without --follow the history would stop at the rename.
        Assert.Equal(2, history.Count);
        Assert.Contains(history, c => c.Subject == "created under the old name");
    }

    [Fact]
    public async Task FileHistoryStopsAtTheRenameWhenFollowingIsOff()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("created under the old name", "old.txt", new string('x', 200));
        fixture.Git("mv", "old.txt", "new.txt");
        fixture.CommitAll("renamed it");

        var repo = await OpenAsync(fixture);
        var history = await repo.Files.GetFileHistoryAsync("new.txt", followRenames: false);

        Assert.Single(history);
    }

    [Fact]
    public async Task FileHistoryOfAnUnknownPathIsEmpty()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        Assert.Empty(await (await OpenAsync(fixture)).Files.GetFileHistoryAsync("nope.txt"));
    }

    // --------------------------------------------------------------- tree

    [Fact]
    public async Task TheTreeListsEveryFileAtARevisionWithItsSize()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.WriteFile("a.txt", "12345");
        fixture.WriteFile("nested/b.txt", "hello");
        fixture.CommitAll("first");

        var repo = await OpenAsync(fixture);
        var tree = await repo.Files.GetTreeAsync();

        Assert.Equal(2, tree.Count);
        Assert.Single(tree, e => e.Path == "a.txt" && e.Size == 5);
        Assert.Single(tree, e => e.Path == "nested/b.txt" && e.Name == "b.txt");
    }

    [Fact]
    public async Task TheTreeReflectsTheRevisionAskedFor()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "b.txt", "two\n");

        var repo = await OpenAsync(fixture);

        Assert.Single(await repo.Files.GetTreeAsync(first));
        Assert.Equal(2, (await repo.Files.GetTreeAsync("HEAD")).Count);
    }

    [Fact]
    public async Task PathsWithSpacesSurviveTreeListing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.WriteFile("my folder/my file.txt", "content");
        fixture.CommitAll("first");

        var tree = await (await OpenAsync(fixture)).Files.GetTreeAsync();

        Assert.Single(tree, e => e.Path == "my folder/my file.txt");
    }

    // -------------------------------------------------------------- blobs

    [Fact]
    public async Task ABlobIsReadBackByteForByte()
    {
        using var fixture = TestRepository.CreateEmpty();
        byte[] bytes = [0x00, 0x01, 0xFF, 0x7F, 0x00, 0x42];
        await File.WriteAllBytesAsync(Path.Combine(fixture.Path, "blob.bin"), bytes);
        fixture.CommitAll("add binary");

        var repo = await OpenAsync(fixture);
        var read = await repo.Files.GetBlobAsync("HEAD", "blob.bin");

        // Reading through a text decoder would mangle these bytes.
        Assert.Equal(bytes, read);
    }

    [Fact]
    public async Task AMissingBlobReturnsNull()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        Assert.Null(await (await OpenAsync(fixture)).Files.GetBlobAsync("HEAD", "nope.bin"));
    }

    [Theory]
    [InlineData("logo.png", true)]
    [InlineData("photo.JPEG", true)]
    [InlineData("icon.ico", true)]
    [InlineData("code.cs", false)]
    [InlineData("noextension", false)]
    public void ImagePathsAreRecognisedByExtension(string path, bool expected)
    {
        Assert.Equal(expected, GitFileOperations.IsImagePath(path));
    }

    // ------------------------------------------------------- diff options

    [Fact]
    public async Task MoreContextLinesShowMoreSurroundingCode()
    {
        using var fixture = TestRepository.CreateEmpty();
        var original = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\n";
        fixture.Commit("first", "a.txt", original);
        fixture.Commit("second", "a.txt",
            original.Replace("line 15\n", "CHANGED\n", StringComparison.Ordinal));

        var repo = await OpenAsync(fixture);
        var commit = (await repo.GetCommitsAsync())[0];
        var file = new FileChange(ChangeKind.Modified, "a.txt");

        var tight = await repo.GetCommitFileDiffStructuredAsync(
            commit.Sha, file, new DiffOptions { ContextLines = 1 });
        var wide = await repo.GetCommitFileDiffStructuredAsync(
            commit.Sha, file, new DiffOptions { ContextLines = 8 });

        Assert.True(wide!.Hunks[0].Lines.Count > tight!.Hunks[0].Lines.Count);
    }

    [Fact]
    public async Task IgnoringWhitespaceHidesAPureReindentation()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "if (x) {\nreturn;\n}\n");
        fixture.Commit("reindented", "a.txt", "if (x) {\n    return;\n}\n");

        var repo = await OpenAsync(fixture);
        var commit = (await repo.GetCommitsAsync())[0];
        var file = new FileChange(ChangeKind.Modified, "a.txt");

        var shown = await repo.GetCommitFileDiffStructuredAsync(
            commit.Sha, file, new DiffOptions { Whitespace = WhitespaceMode.Show });
        var ignored = await repo.GetCommitFileDiffStructuredAsync(
            commit.Sha, file, new DiffOptions { Whitespace = WhitespaceMode.IgnoreAll });

        Assert.NotEmpty(shown!.Hunks);

        // Nothing to show is a real answer here, distinct from failing to read the diff.
        Assert.NotNull(ignored);
        Assert.Empty(ignored.Hunks);
    }

    [Fact]
    public void DiffOptionsProduceTheExpectedGitFlags()
    {
        var options = new DiffOptions
        {
            ContextLines = 7,
            Whitespace = WhitespaceMode.IgnoreChange,
            DetectRenames = true,
        };

        var args = options.ToArguments().ToList();

        Assert.Contains("--unified=7", args);
        Assert.Contains("--ignore-space-change", args);
        Assert.Contains("-M", args);
    }

    [Fact]
    public void NegativeContextIsClampedRatherThanPassedToGit()
    {
        var args = new DiffOptions { ContextLines = -5 }.ToArguments().ToList();

        Assert.Contains("--unified=0", args);
    }
}
