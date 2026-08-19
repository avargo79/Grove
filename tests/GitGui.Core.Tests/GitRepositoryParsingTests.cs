using GitGui.Core;

namespace GitGui.Core.Tests;

public class GitRepositoryParsingTests
{
    // ------------------------------------------------- --name-status -z

    [Fact]
    public void NameStatusParsesASingleModifiedFile()
    {
        var change = Assert.Single(GitRepository.ParseNameStatusZ("M\0src/app.cs\0"));

        Assert.Equal(ChangeKind.Modified, change.Kind);
        Assert.Equal("src/app.cs", change.Path);
        Assert.Null(change.OldPath);
    }

    [Fact]
    public void NameStatusParsesSeveralFilesInOrder()
    {
        var changes = GitRepository.ParseNameStatusZ("A\0added.txt\0M\0changed.txt\0D\0gone.txt\0");

        Assert.Equal(3, changes.Count);
        Assert.Equal(ChangeKind.Added, changes[0].Kind);
        Assert.Equal(ChangeKind.Modified, changes[1].Kind);
        Assert.Equal(ChangeKind.Deleted, changes[2].Kind);
        Assert.Equal("gone.txt", changes[2].Path);
    }

    [Fact]
    public void NameStatusParsesARenameWithBothPaths()
    {
        // Renames carry a similarity score and consume two path fields.
        var change = Assert.Single(GitRepository.ParseNameStatusZ("R096\0old/name.cs\0new/name.cs\0"));

        Assert.Equal(ChangeKind.Renamed, change.Kind);
        Assert.Equal("old/name.cs", change.OldPath);
        Assert.Equal("new/name.cs", change.Path);
        Assert.Equal("old/name.cs → new/name.cs", change.DisplayPath);
    }

    [Fact]
    public void NameStatusKeepsReadingAfterARename()
    {
        var changes = GitRepository.ParseNameStatusZ("R100\0a.txt\0b.txt\0M\0c.txt\0");

        Assert.Equal(2, changes.Count);
        Assert.Equal("b.txt", changes[0].Path);
        Assert.Equal("c.txt", changes[1].Path);
    }

    [Fact]
    public void NameStatusHandlesPathsContainingSpaces()
    {
        var change = Assert.Single(GitRepository.ParseNameStatusZ("M\0docs/my notes.md\0"));

        Assert.Equal("docs/my notes.md", change.Path);
    }

    [Fact]
    public void NameStatusIgnoresATruncatedTrailingRecord()
    {
        // A status code with no path behind it must not throw or produce a bogus entry.
        var changes = GitRepository.ParseNameStatusZ("M\0first.txt\0M\0");

        Assert.Single(changes);
    }

    [Fact]
    public void NameStatusOfAnEmptyCommitIsEmpty()
    {
        Assert.Empty(GitRepository.ParseNameStatusZ(string.Empty));
    }

    // -------------------------------------------------------- decoration

    [Fact]
    public void DecorationOfAnUndecoratedCommitIsEmpty()
    {
        Assert.Empty(GitRepository.ParseDecoration(string.Empty));
        Assert.Empty(GitRepository.ParseDecoration("   "));
    }

    [Fact]
    public void DecorationSplitsHeadPointerIntoBothRefs()
    {
        var names = GitRepository.ParseDecoration("HEAD -> main, origin/main");

        Assert.Equal(["HEAD", "main", "origin/main"], names);
    }

    [Fact]
    public void DecorationKeepsTheTagPrefixSoTagsStayDistinguishable()
    {
        var names = GitRepository.ParseDecoration("tag: v1.2.0, main");

        Assert.Equal(["tag: v1.2.0", "main"], names);
    }

    [Fact]
    public void DecorationHandlesDetachedHeadWithoutABranch()
    {
        var names = GitRepository.ParseDecoration("HEAD");

        Assert.Equal(["HEAD"], names);
    }

    [Fact]
    public void DecorationTrimsSurroundingWhitespace()
    {
        var names = GitRepository.ParseDecoration("main,  origin/main ,  tag: v2 ");

        Assert.Equal(["main", "origin/main", "tag: v2"], names);
    }

    // ----------------------------------------------------- upstream track

    [Theory]
    [InlineData("[ahead 2]", 2, 0)]
    [InlineData("[behind 3]", 0, 3)]
    [InlineData("[ahead 1, behind 4]", 1, 4)]
    [InlineData("[behind 4, ahead 1]", 1, 4)]
    public void TrackParsesAheadAndBehindCounts(string track, int expectedAhead, int expectedBehind)
    {
        var (ahead, behind) = GitRepository.ParseTrack(track);

        Assert.Equal(expectedAhead, ahead);
        Assert.Equal(expectedBehind, behind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[gone]")]
    [InlineData("[up to date]")]
    public void TrackReportsNoDivergenceWhenThereIsNothingToCount(string track)
    {
        var (ahead, behind) = GitRepository.ParseTrack(track);

        Assert.Equal(0, ahead);
        Assert.Equal(0, behind);
    }

    // -------------------------------------------------------- ref helpers

    [Fact]
    public void RemoteBranchExposesItsRemoteAndBareName()
    {
        var gitRef = new GitRef("refs/remotes/origin/feature/login", "origin/feature/login",
            RefKind.RemoteBranch, "abc123", null, 0, 0, false);

        Assert.Equal("origin", gitRef.RemoteName);
        Assert.Equal("feature/login", gitRef.NameWithinRemote);
    }

    [Fact]
    public void LocalBranchHasNoRemoteAndKeepsItsFullName()
    {
        var gitRef = new GitRef("refs/heads/feature/login", "feature/login",
            RefKind.LocalBranch, "abc123", "origin/feature/login", 1, 0, true);

        Assert.Null(gitRef.RemoteName);
        Assert.Equal("feature/login", gitRef.NameWithinRemote);
    }

    // ------------------------------------------------------------ models

    [Fact]
    public void ShortShaIsTheFirstSevenCharacters()
    {
        var commit = new Commit("0123456789abcdef", [], "A", "a@b", DateTimeOffset.UnixEpoch,
            "A", DateTimeOffset.UnixEpoch, "subject", []);

        Assert.Equal("0123456", commit.ShortSha);
        Assert.False(commit.IsMerge);
    }

    [Fact]
    public void CommitWithTwoParentsIsAMerge()
    {
        var commit = new Commit("abc", ["p1", "p2"], "A", "a@b", DateTimeOffset.UnixEpoch,
            "A", DateTimeOffset.UnixEpoch, "merge", []);

        Assert.True(commit.IsMerge);
    }

    [Fact]
    public void FileChangeSplitsDirectoryFromFileName()
    {
        var change = new FileChange(ChangeKind.Modified, "src/deep/path/File.cs");

        Assert.Equal("File.cs", change.FileName);
        Assert.Equal("src/deep/path", change.Directory);
    }

    [Fact]
    public void EmptyStatusIsReportedAsClean()
    {
        Assert.True(WorkingTreeStatus.Empty.IsClean);
        Assert.Equal(0, WorkingTreeStatus.Empty.TotalChanges);
    }
}
