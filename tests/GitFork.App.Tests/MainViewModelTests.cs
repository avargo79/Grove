using GitFork.App.ViewModels;
using GitFork.Core;
using GitFork.Core.Tests;

namespace GitFork.App.Tests;

/// <summary>
/// Drives the main view model against real throwaway repositories. These cover the wiring that
/// the pure-Core tests cannot see: selection side effects, sidebar grouping and status text.
/// </summary>
public class MainViewModelTests
{
    /// <summary>main and feature diverge and are merged back, with a tag and a stash present.</summary>
    private static TestRepository CreateRichRepository()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base commit", "README.md", "hello\nworld\n");
        fixture.Git("tag", "v0.1.0");

        fixture.Git("checkout", "--quiet", "-b", "feature/login");
        fixture.Commit("add login", "src/login.cs", "class Login {}\n");

        fixture.Git("checkout", "--quiet", "main");
        fixture.Commit("update readme", "README.md", "hello\nchanged\n");
        fixture.Git("merge", "--quiet", "--no-ff", "feature/login", "-m", "merge login");

        fixture.WriteFile("README.md", "work in progress\n");
        fixture.Git("stash", "push", "--quiet", "-m", "wip");
        return fixture;
    }

    /// <summary>Watching is off in tests: a background refresh would race the assertions.</summary>
    private static MainViewModel NewViewModel() => new() { WatchForChanges = false };

    private static async Task<MainViewModel> LoadAsync(TestRepository fixture)
    {
        var vm = NewViewModel();
        await vm.LoadRepositoryAsync(fixture.Path);
        await vm.PendingDetailLoad;
        await vm.PendingDiffLoad;
        return vm;
    }

    [Fact]
    public async Task OpeningANonRepositoryReportsItInTheStatusBarWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitfork-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var vm = NewViewModel();
            await vm.LoadRepositoryAsync(path);

            Assert.False(vm.HasRepository);
            Assert.Empty(vm.Commits);
            Assert.Contains("not inside a git repository", vm.StatusMessage);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task OpeningARepositoryPopulatesTheCommitListAndHeader()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        Assert.True(vm.HasRepository);
        Assert.Equal(4, vm.Commits.Count);
        Assert.Equal("main", vm.CurrentBranch);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task EveryCommitRowSharesTheSameGraphColumnWidth()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        // Lanes must line up between rows, so the width is computed once for the whole list.
        Assert.Single(vm.Commits.Select(c => c.GraphWidth).Distinct());
        Assert.True(vm.Commits[0].GraphWidth > 0);
    }

    [Fact]
    public async Task TheMergeCommitIsRenderedAsAMergeOnMoreThanOneLane()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        var merge = Assert.Single(vm.Commits, c => c.GraphRow.IsMerge);
        Assert.Equal("merge login", merge.Subject);
        Assert.True(vm.Commits.Max(c => c.GraphRow.LaneCount) >= 2);
    }

    [Fact]
    public async Task HeadCommitCarriesHeadAndBranchBadges()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        var head = vm.Commits[0];
        Assert.True(head.HasBadges);
        Assert.Contains(head.Badges, b => b.Kind == RefBadgeKind.Head);
        Assert.Contains(head.Badges, b => b is { Kind: RefBadgeKind.LocalBranch, Name: "main" });
    }

    [Fact]
    public async Task ASlashedBranchNameIsBadgedAsLocalNotRemote()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        // "feature/login" contains a slash but is an ordinary local branch.
        var tip = Assert.Single(vm.Commits, c => c.Subject == "add login");
        var badge = Assert.Single(tip.Badges);
        Assert.Equal(RefBadgeKind.LocalBranch, badge.Kind);
        Assert.Equal("feature/login", badge.Name);
    }

    [Fact]
    public async Task StashCommitsAreKeptOutOfTheHistoryList()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        // The fixture stashes work in progress; git's internal stash commits must not appear.
        Assert.DoesNotContain(vm.Commits, c => c.Subject.Contains("wip", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Commits, c => c.Subject.StartsWith("index on ", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Commits, c => c.Badges.Any(b => b.Kind == RefBadgeKind.Stash));

        // ...but the stash is still listed in the sidebar.
        Assert.Single(vm.Sections.Single(s => s.Title == "Stashes").Items);
    }

    [Fact]
    public async Task TagBadgesAreLabelledWithoutTheTagPrefix()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        var tagged = Assert.Single(vm.Commits, c => c.Badges.Any(b => b.Kind == RefBadgeKind.Tag));
        var badge = Assert.Single(tagged.Badges, b => b.Kind == RefBadgeKind.Tag);
        Assert.Equal("v0.1.0", badge.Name);
    }

    [Fact]
    public async Task SidebarGroupsBranchesTagsAndStashesIntoSections()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        var titles = vm.Sections.Select(s => s.Title).ToList();
        Assert.Contains("Branches", titles);
        Assert.Contains("Tags", titles);
        Assert.Contains("Stashes", titles);
        // No remote is configured, so that section must not appear at all.
        Assert.DoesNotContain("Remotes", titles);
    }

    [Fact]
    public async Task TheCheckedOutBranchIsFlaggedInTheSidebar()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        var branches = vm.Sections.Single(s => s.Title == "Branches");
        Assert.Equal(2, branches.Items.Count);
        Assert.Single(branches.Items, i => i.IsHead && i.DisplayName == "main");
    }

    [Fact]
    public async Task SelectingARefInTheSidebarSelectsItsTipCommit()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        var feature = vm.Sections.Single(s => s.Title == "Branches")
            .Items.Single(i => i.DisplayName == "feature/login");

        vm.SelectCommitBySha(feature.TargetSha);

        Assert.Equal(feature.TargetSha, vm.SelectedCommit!.Sha);
        Assert.Equal("add login", vm.SelectedCommit.Subject);
    }

    [Fact]
    public async Task TheNewestCommitIsSelectedAndItsDetailLoadedOnOpen()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        Assert.NotNull(vm.SelectedCommit);
        Assert.NotNull(vm.Detail);
        Assert.Equal(vm.SelectedCommit!.Sha, vm.Detail!.Sha);
    }

    [Fact]
    public async Task SelectingACommitLoadsItsFilesAndTheFirstFilesDiff()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        vm.SelectedCommit = vm.Commits.Single(c => c.Subject == "update readme");
        await vm.PendingDetailLoad;
        await vm.PendingDiffLoad;

        var file = Assert.Single(vm.Detail!.Files);
        Assert.Equal("README.md", file.Change.Path);
        Assert.Same(file, vm.Detail.SelectedFile);
        Assert.Contains(vm.Detail!.DiffLines, l => l.IsAdded && l.Text == "changed");
        Assert.Contains(vm.Detail!.DiffLines, l => l.IsRemoved && l.Text == "world");
    }

    [Fact]
    public async Task ChangingTheSelectedFileReloadsTheDiff()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "ONE\n");
        fixture.WriteFile("b.txt", "two\n");
        fixture.CommitAll("touch both");

        var vm = await LoadAsync(fixture);
        Assert.Equal(2, vm.Detail!.Files.Count);

        vm.Detail.SelectedFile = vm.Detail.Files.Single(f => f.Change.Path == "b.txt");
        await vm.PendingDiffLoad;

        Assert.Contains(vm.Detail!.DiffLines, l => l.IsAdded && l.Text == "two");
        Assert.DoesNotContain(vm.Detail!.DiffLines, l => l.Text == "ONE");
    }

    [Fact]
    public async Task DeselectingAllCommitsClearsTheDetailPane()
    {
        using var fixture = CreateRichRepository();
        var vm = await LoadAsync(fixture);

        vm.SelectedCommit = null;
        await vm.PendingDetailLoad;

        Assert.Null(vm.Detail);
    }

    [Fact]
    public async Task TheStatusBarReportsACleanTreeWithTheCommitCount()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("only", "a.txt", "1");

        var vm = await LoadAsync(fixture);

        Assert.Equal("1 commit · working tree clean", vm.StatusMessage);
    }

    [Fact]
    public async Task TheStatusBarBreaksDownStagedChangedAndUntrackedCounts()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "tracked.txt", "1");
        fixture.WriteFile("staged.txt", "new");
        fixture.Git("add", "staged.txt");
        fixture.WriteFile("tracked.txt", "changed");
        fixture.WriteFile("loose.txt", "untracked");

        var vm = await LoadAsync(fixture);

        Assert.Contains("1 staged", vm.StatusMessage);
        Assert.Contains("1 changed", vm.StatusMessage);
        Assert.Contains("1 untracked", vm.StatusMessage);
    }

    [Fact]
    public async Task RefreshPicksUpCommitsMadeOutsideTheApp()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");

        var vm = await LoadAsync(fixture);
        Assert.Single(vm.Commits);

        fixture.Commit("second", "a.txt", "2");
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.PendingDetailLoad;

        Assert.Equal(2, vm.Commits.Count);
        Assert.Equal("second", vm.Commits[0].Subject);
    }

    [Fact]
    public async Task AnEmptyRepositoryLoadsWithNoCommitsAndNoDetail()
    {
        using var fixture = TestRepository.CreateEmpty();

        var vm = await LoadAsync(fixture);

        Assert.True(vm.HasRepository);
        Assert.Empty(vm.Commits);
        Assert.Null(vm.Detail);
    }

    [Fact]
    public async Task OpenRepositoryUsesThePickerSuppliedByTheView()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "1");

        var vm = new MainViewModel { WatchForChanges = false, PickFolderAsync = () => Task.FromResult<string?>(fixture.Path) };
        await vm.OpenRepositoryCommand.ExecuteAsync(null);
        await vm.PendingDetailLoad;

        Assert.True(vm.HasRepository);
        Assert.Single(vm.Commits);
    }

    [Fact]
    public async Task CancellingThePickerLeavesNoRepositoryOpen()
    {
        var vm = new MainViewModel { WatchForChanges = false, PickFolderAsync = () => Task.FromResult<string?>(null) };

        await vm.OpenRepositoryCommand.ExecuteAsync(null);

        Assert.False(vm.HasRepository);
        Assert.Empty(vm.Commits);
    }
}
