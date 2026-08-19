using GitGui.App.ViewModels;
using GitGui.Core;
using GitGui.Core.Tests;

namespace GitGui.App.Tests;

/// <summary>
/// Drives every repository action the UI exposes, against real repositories. The emphasis is on
/// what reaches git and what is gated behind a confirmation.
/// </summary>
public class RepositoryCommandsTests
{
    private static async Task<MainViewModel> LoadAsync(TestRepository fixture, bool confirmEverything = true)
    {
        var vm = new MainViewModel { WatchForChanges = false };
        await vm.LoadRepositoryAsync(fixture.Path);
        await vm.PendingDetailLoad;

        if (confirmEverything)
            vm.Commands!.ConfirmAsync = _ => Task.FromResult(true);

        return vm;
    }

    /// <summary>Runs the fire-and-forget command body to completion, then reloads.</summary>
    private static async Task SettleAsync(MainViewModel vm)
    {
        await vm.Commands!.PendingOperation;
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.PendingDetailLoad;
    }

    private static SidebarItemViewModel Ref(MainViewModel vm, string section, string name) =>
        vm.Sections.Single(s => s.Title == section).Items.Single(i => i.DisplayName == name);

    // ------------------------------------------------------------ checkout

    [Fact]
    public async Task CheckingOutABranchFromTheSidebarSwitchesToIt()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "feature");

        var vm = await LoadAsync(fixture);
        await vm.Commands!.CheckoutRefCommand.ExecuteAsync(Ref(vm, "Branches", "feature"));
        await SettleAsync(vm);

        Assert.Equal("feature", vm.CurrentBranch);
    }

    [Fact]
    public async Task CheckingOutACommitDetachesHead()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var vm = await LoadAsync(fixture);
        var older = vm.Commits.Single(c => c.Subject == "first");

        await vm.Commands!.CheckoutCommitCommand.ExecuteAsync(older);
        await SettleAsync(vm);

        Assert.Equal("detached HEAD", vm.CurrentBranch);
    }

    // ------------------------------------------------------ branch lifecycle

    [Fact]
    public async Task CreatingABranchUsesTheNameTheUserTyped()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var vm = await LoadAsync(fixture);
        vm.Commands!.PromptAsync = _ => Task.FromResult<string?>("shiny-new-branch");

        await vm.Commands.CreateBranchCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.Equal("shiny-new-branch", vm.CurrentBranch);
    }

    [Fact]
    public async Task CancellingTheNamePromptCreatesNothing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var vm = await LoadAsync(fixture);
        vm.Commands!.PromptAsync = _ => Task.FromResult<string?>(null);

        await vm.Commands.CreateBranchCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.Equal("main", vm.CurrentBranch);
        Assert.Single(vm.Sections.Single(s => s.Title == "Branches").Items);
    }

    [Fact]
    public async Task CreatingABranchAtACommitStartsItThere()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var vm = await LoadAsync(fixture);
        vm.Commands!.PromptAsync = _ => Task.FromResult<string?>("from-first");

        await vm.Commands.CreateBranchCommand.ExecuteAsync(first);
        await SettleAsync(vm);

        Assert.Equal(first, fixture.Head());
    }

    [Fact]
    public async Task RenamingABranchAppliesTheNewName()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "old-name");

        var vm = await LoadAsync(fixture);
        vm.Commands!.PromptAsync = _ => Task.FromResult<string?>("better-name");

        await vm.Commands.RenameBranchCommand.ExecuteAsync(Ref(vm, "Branches", "old-name"));
        await SettleAsync(vm);

        var names = vm.Sections.Single(s => s.Title == "Branches").Items.Select(i => i.DisplayName);
        Assert.Contains("better-name", names);
        Assert.DoesNotContain("old-name", names);
    }

    [Fact]
    public async Task DeletingABranchIsBlockedUntilConfirmed()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "keep-me");

        var vm = await LoadAsync(fixture, confirmEverything: false);
        var asked = false;
        vm.Commands!.ConfirmAsync = _ => { asked = true; return Task.FromResult(false); };

        await vm.Commands.DeleteBranchCommand.ExecuteAsync(Ref(vm, "Branches", "keep-me"));
        await SettleAsync(vm);

        Assert.True(asked);
        Assert.Contains(vm.Sections.Single(s => s.Title == "Branches").Items, i => i.DisplayName == "keep-me");
    }

    [Fact]
    public async Task DeletingAnUnmergedBranchWarnsThatCommitsWillBeLost()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("checkout", "--quiet", "-b", "unmerged");
        fixture.Commit("only here", "b.txt", "two\n");
        fixture.Git("checkout", "--quiet", "main");

        var vm = await LoadAsync(fixture, confirmEverything: false);
        string? prompt = null;
        vm.Commands!.ConfirmAsync = message => { prompt = message; return Task.FromResult(true); };

        await vm.Commands.DeleteBranchCommand.ExecuteAsync(Ref(vm, "Branches", "unmerged"));
        await SettleAsync(vm);

        Assert.Contains("exist nowhere else", prompt);
        Assert.DoesNotContain(vm.Sections.Single(s => s.Title == "Branches").Items, i => i.DisplayName == "unmerged");
    }

    [Fact]
    public async Task DeletingAMergedBranchDoesNotWarnAboutLostCommits()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "merged-already");

        var vm = await LoadAsync(fixture, confirmEverything: false);
        string? prompt = null;
        vm.Commands!.ConfirmAsync = message => { prompt = message; return Task.FromResult(true); };

        await vm.Commands.DeleteBranchCommand.ExecuteAsync(Ref(vm, "Branches", "merged-already"));
        await SettleAsync(vm);

        Assert.DoesNotContain("exist nowhere else", prompt);
    }

    // ---------------------------------------------------------------- tags

    [Fact]
    public async Task CreatingATagAtACommitPlacesItThere()
    {
        using var fixture = TestRepository.CreateEmpty();
        var sha = fixture.Commit("first", "a.txt", "one\n");

        var vm = await LoadAsync(fixture);
        vm.Commands!.PromptAsync = _ => Task.FromResult<string?>("v1.0.0");

        await vm.Commands.CreateTagCommand.ExecuteAsync(sha);
        await SettleAsync(vm);

        Assert.Contains(vm.Sections.Single(s => s.Title == "Tags").Items, i => i.DisplayName == "v1.0.0");
    }

    [Fact]
    public async Task DeletingATagRequiresConfirmation()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("tag", "v1.0.0");

        var vm = await LoadAsync(fixture, confirmEverything: false);
        vm.Commands!.ConfirmAsync = _ => Task.FromResult(false);

        await vm.Commands.DeleteTagCommand.ExecuteAsync(Ref(vm, "Tags", "v1.0.0"));
        await SettleAsync(vm);

        Assert.Contains(vm.Sections.Single(s => s.Title == "Tags").Items, i => i.DisplayName == "v1.0.0");
    }

    // --------------------------------------------------------- merge/rebase

    private static TestRepository CreateConflictingBranches()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "shared.txt", "original\n");
        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("feature edit", "shared.txt", "from feature\n");
        fixture.Git("checkout", "--quiet", "main");
        fixture.Commit("main edit", "shared.txt", "from main\n");
        return fixture;
    }

    [Fact]
    public async Task MergingACleanBranchReportsSuccess()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "a.txt", "one\n");
        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("feature work", "b.txt", "two\n");
        fixture.Git("checkout", "--quiet", "main");

        var vm = await LoadAsync(fixture);
        await vm.Commands!.MergeRefCommand.ExecuteAsync(Ref(vm, "Branches", "feature"));
        await SettleAsync(vm);

        Assert.False(vm.Commands.IsError);
        Assert.False(vm.Commands.IsOperationInProgress);
        Assert.True(File.Exists(Path.Combine(fixture.Path, "b.txt")));
    }

    [Fact]
    public async Task AConflictingMergeRaisesTheBannerRatherThanAnError()
    {
        using var fixture = CreateConflictingBranches();
        var vm = await LoadAsync(fixture);

        await vm.Commands!.MergeRefCommand.ExecuteAsync(Ref(vm, "Branches", "feature"));
        await SettleAsync(vm);

        // Conflicts are a state to resolve, so this must not read as a failure.
        Assert.False(vm.Commands.IsError);
        Assert.True(vm.Commands.IsOperationInProgress);
        Assert.Equal("Merge in progress", vm.Commands.OperationDescription);
        Assert.Equal("1 file still has conflicts.", vm.Commands.ConflictSummary);
    }

    [Fact]
    public async Task AbortingClearsTheBanner()
    {
        using var fixture = CreateConflictingBranches();
        var vm = await LoadAsync(fixture);
        await vm.Commands!.MergeRefCommand.ExecuteAsync(Ref(vm, "Branches", "feature"));
        await SettleAsync(vm);

        await vm.Commands.AbortOperationCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.False(vm.Commands.IsOperationInProgress);
        Assert.Equal("from main\n", fixture.WorkingContent("shared.txt"));
    }

    [Fact]
    public async Task AbortingIsBlockedUntilConfirmed()
    {
        using var fixture = CreateConflictingBranches();
        var vm = await LoadAsync(fixture, confirmEverything: false);
        vm.Commands!.ConfirmAsync = _ => Task.FromResult(true);
        await vm.Commands.MergeRefCommand.ExecuteAsync(Ref(vm, "Branches", "feature"));
        await SettleAsync(vm);

        vm.Commands.ConfirmAsync = _ => Task.FromResult(false);
        await vm.Commands.AbortOperationCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.True(vm.Commands.IsOperationInProgress);
    }

    [Fact]
    public async Task ContinuingAfterResolvingCompletesTheMerge()
    {
        using var fixture = CreateConflictingBranches();
        var vm = await LoadAsync(fixture);
        await vm.Commands!.MergeRefCommand.ExecuteAsync(Ref(vm, "Branches", "feature"));
        await SettleAsync(vm);

        fixture.WriteFile("shared.txt", "resolved\n");
        fixture.Git("add", "shared.txt");

        await vm.Commands.ContinueOperationCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.False(vm.Commands.IsOperationInProgress);
        Assert.Contains(vm.Commits, c => c.Commit.IsMerge);
    }

    [Fact]
    public async Task ContinuingWithConflictsLeftSaysSoWithoutCommitting()
    {
        using var fixture = CreateConflictingBranches();
        var vm = await LoadAsync(fixture);
        await vm.Commands!.MergeRefCommand.ExecuteAsync(Ref(vm, "Branches", "feature"));
        await SettleAsync(vm);

        await vm.Commands.ContinueOperationCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.True(vm.Commands.IsOperationInProgress);
        Assert.Contains("Resolve the remaining conflicts", vm.Commands.StatusText);
    }

    [Fact]
    public async Task RebasingIsBlockedUntilConfirmed()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "a.txt", "one\n");
        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("feature work", "b.txt", "two\n");

        var vm = await LoadAsync(fixture, confirmEverything: false);
        var asked = false;
        vm.Commands!.ConfirmAsync = _ => { asked = true; return Task.FromResult(false); };

        await vm.Commands.RebaseOntoRefCommand.ExecuteAsync(Ref(vm, "Branches", "main"));
        await SettleAsync(vm);

        Assert.True(asked);
        Assert.Equal(2, fixture.CommitCount());
    }

    // ------------------------------------------------- cherry-pick / revert

    [Fact]
    public async Task CherryPickingBringsACommitOntoTheCurrentBranch()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "a.txt", "one\n");
        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("feature work", "b.txt", "two\n");
        fixture.Git("checkout", "--quiet", "main");

        var vm = await LoadAsync(fixture);
        var row = vm.Commits.Single(c => c.Subject == "feature work");

        await vm.Commands!.CherryPickCommand.ExecuteAsync(row);
        await SettleAsync(vm);

        Assert.True(File.Exists(Path.Combine(fixture.Path, "b.txt")));
    }

    [Fact]
    public async Task RevertingAddsAnInverseCommit()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("adds a file", "b.txt", "two\n");

        var vm = await LoadAsync(fixture);
        var row = vm.Commits.Single(c => c.Subject == "adds a file");

        await vm.Commands!.RevertCommand.ExecuteAsync(row);
        await SettleAsync(vm);

        Assert.False(File.Exists(Path.Combine(fixture.Path, "b.txt")));
        Assert.Equal(3, fixture.CommitCount());
    }

    [Fact]
    public async Task RevertingAMergeSuppliesTheMainlineParentAutomatically()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("base", "a.txt", "one\n");
        fixture.Git("checkout", "--quiet", "-b", "feature");
        fixture.Commit("feature work", "b.txt", "two\n");
        fixture.Git("checkout", "--quiet", "main");
        fixture.Git("merge", "--quiet", "--no-ff", "feature", "-m", "merge feature");

        var vm = await LoadAsync(fixture);
        var merge = vm.Commits.Single(c => c.Commit.IsMerge);

        // Without -m git refuses outright, so this would fail if the mainline were not passed.
        await vm.Commands!.RevertCommand.ExecuteAsync(merge);
        await SettleAsync(vm);

        Assert.False(vm.Commands.IsError);
        Assert.False(File.Exists(Path.Combine(fixture.Path, "b.txt")));
    }

    // --------------------------------------------------------------- reset

    [Fact]
    public async Task ASoftResetNeedsNoConfirmationBecauseNothingIsLost()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var vm = await LoadAsync(fixture, confirmEverything: false);
        var asked = false;
        vm.Commands!.ConfirmAsync = _ => { asked = true; return Task.FromResult(true); };

        await vm.Commands.ResetSoftCommand.ExecuteAsync(vm.Commits.Single(c => c.Sha == first));
        await SettleAsync(vm);

        Assert.False(asked);
        Assert.Equal(first, fixture.Head());
    }

    [Fact]
    public async Task AHardResetIsBlockedUntilConfirmed()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var vm = await LoadAsync(fixture, confirmEverything: false);
        vm.Commands!.ConfirmAsync = _ => Task.FromResult(false);

        await vm.Commands.ResetHardCommand.ExecuteAsync(vm.Commits.Single(c => c.Sha == first));
        await SettleAsync(vm);

        Assert.Equal(2, fixture.CommitCount());
        Assert.Equal("two\n", fixture.WorkingContent("a.txt"));
    }

    [Fact]
    public async Task AHardResetWithNoConfirmationHookRefusesRatherThanProceeding()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var vm = await LoadAsync(fixture, confirmEverything: false);
        vm.Commands!.ConfirmAsync = null;

        await vm.Commands.ResetHardCommand.ExecuteAsync(vm.Commits.Single(c => c.Sha == first));
        await SettleAsync(vm);

        // Silence must never be read as consent for something unrecoverable.
        Assert.Equal(2, fixture.CommitCount());
    }

    [Fact]
    public async Task AConfirmedHardResetDiscardsEverything()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var vm = await LoadAsync(fixture);
        await vm.Commands!.ResetHardCommand.ExecuteAsync(vm.Commits.Single(c => c.Sha == first));
        await SettleAsync(vm);

        Assert.Equal(first, fixture.Head());
        Assert.Equal("one\n", fixture.WorkingContent("a.txt"));
    }

    // --------------------------------------------------------------- stash

    [Fact]
    public async Task EveryStashIsListedInTheSidebarNotJustTheMostRecent()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "older\n");
        fixture.Git("stash", "push", "--quiet", "-m", "older stash");
        fixture.WriteFile("a.txt", "newer\n");
        fixture.Git("stash", "push", "--quiet", "-m", "newer stash");

        var vm = await LoadAsync(fixture);
        var stashes = vm.Sections.Single(s => s.Title == "Stashes").Items;

        // for-each-ref only reports refs/stash, so reading the reflog is what makes both visible.
        Assert.Equal(2, stashes.Count);
        Assert.Equal("stash@{0}", stashes[0].Ref.FullName);
        Assert.Equal("stash@{1}", stashes[1].Ref.FullName);
    }

    [Fact]
    public async Task PoppingTheOlderStashTargetsTheRightOne()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "older\n");
        fixture.Git("stash", "push", "--quiet", "-m", "older stash");
        fixture.WriteFile("a.txt", "newer\n");
        fixture.Git("stash", "push", "--quiet", "-m", "newer stash");

        var vm = await LoadAsync(fixture);
        var older = vm.Sections.Single(s => s.Title == "Stashes").Items[1];

        await vm.Commands!.StashPopCommand.ExecuteAsync(older);
        await SettleAsync(vm);

        Assert.Equal("older\n", fixture.WorkingContent("a.txt"));
        Assert.Single(vm.Sections.Single(s => s.Title == "Stashes").Items);
    }

    [Fact]
    public async Task StashingUsesTheDescriptionTheUserTyped()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "work\n");

        var vm = await LoadAsync(fixture);
        vm.Commands!.PromptAsync = _ => Task.FromResult<string?>("half-done thing");

        await vm.Commands.StashPushCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        var stash = Assert.Single(vm.Sections.Single(s => s.Title == "Stashes").Items);
        Assert.Contains("half-done thing", stash.DisplayName, StringComparison.Ordinal);
        Assert.False(vm.HasUncommittedChanges);
    }

    [Fact]
    public async Task DroppingAStashRequiresConfirmation()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.WriteFile("a.txt", "work\n");
        fixture.Git("stash", "push", "--quiet", "-m", "keep me");

        var vm = await LoadAsync(fixture, confirmEverything: false);
        vm.Commands!.ConfirmAsync = _ => Task.FromResult(false);

        await vm.Commands.StashDropCommand.ExecuteAsync(
            vm.Sections.Single(s => s.Title == "Stashes").Items[0]);
        await SettleAsync(vm);

        Assert.Single(vm.Sections.Single(s => s.Title == "Stashes").Items);
    }

    // ------------------------------------------------------------- network

    [Fact]
    public async Task FetchingFromALocalRemoteBringsDownNewCommits()
    {
        using var origin = TestRepository.CreateBareRemote();
        using var local = TestRepository.CreateEmpty();
        local.Commit("first", "a.txt", "one\n");
        local.AddRemote(origin);
        local.Git("push", "--quiet", "--set-upstream", "origin", "main");

        using var other = TestRepository.CreateEmpty();
        other.Git("remote", "add", "origin", origin.Path);
        other.Git("fetch", "--quiet", "origin");
        other.Git("checkout", "--quiet", "-B", "main", "origin/main");
        other.Commit("from elsewhere", "b.txt", "two\n");
        other.Git("push", "--quiet", "origin", "main");

        var vm = await LoadAsync(local);
        await vm.Commands!.FetchCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.False(vm.Commands.IsError);
        Assert.Contains(vm.Commits, c => c.Subject == "from elsewhere");
        Assert.False(vm.Commands.IsNetworkOperationRunning);
    }

    [Fact]
    public async Task PushingSendsCommitsToTheRemote()
    {
        using var origin = TestRepository.CreateBareRemote();
        using var local = TestRepository.CreateEmpty();
        local.Commit("first", "a.txt", "one\n");
        local.AddRemote(origin);
        local.Git("push", "--quiet", "--set-upstream", "origin", "main");
        local.Commit("second", "b.txt", "two\n");

        var vm = await LoadAsync(local);
        await vm.Commands!.PushCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.False(vm.Commands.IsError);
        Assert.Contains("second", origin.Git("log", "--format=%s", "main"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushingABranchWithNoUpstreamOffersToCreateIt()
    {
        using var origin = TestRepository.CreateBareRemote();
        using var local = TestRepository.CreateEmpty();
        local.Commit("first", "a.txt", "one\n");
        local.AddRemote(origin);
        local.Git("push", "--quiet", "--set-upstream", "origin", "main");
        local.Git("checkout", "--quiet", "-b", "brand-new");
        local.Commit("work", "c.txt", "three\n");

        var vm = await LoadAsync(local);
        string? prompt = null;
        vm.Commands!.ConfirmAsync = message => { prompt = message; return Task.FromResult(true); };

        await vm.Commands.PushCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.Contains("does not exist on the remote yet", prompt);
        Assert.Contains("brand-new", origin.Git("branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecliningToCreateTheUpstreamPushesNothing()
    {
        using var origin = TestRepository.CreateBareRemote();
        using var local = TestRepository.CreateEmpty();
        local.Commit("first", "a.txt", "one\n");
        local.AddRemote(origin);
        local.Git("push", "--quiet", "--set-upstream", "origin", "main");
        local.Git("checkout", "--quiet", "-b", "brand-new");
        local.Commit("work", "c.txt", "three\n");

        var vm = await LoadAsync(local, confirmEverything: false);
        vm.Commands!.ConfirmAsync = _ => Task.FromResult(false);

        await vm.Commands.PushCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.DoesNotContain("brand-new", origin.Git("branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForcePushingIsBlockedUntilConfirmed()
    {
        using var origin = TestRepository.CreateBareRemote();
        using var local = TestRepository.CreateEmpty();
        local.Commit("first", "a.txt", "one\n");
        local.AddRemote(origin);
        local.Git("push", "--quiet", "--set-upstream", "origin", "main");

        var vm = await LoadAsync(local, confirmEverything: false);
        var asked = false;
        vm.Commands!.ConfirmAsync = _ => { asked = true; return Task.FromResult(false); };

        await vm.Commands.ForcePushCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        Assert.True(asked);
    }

    [Fact]
    public async Task FetchingWithNoRemotesConfiguredIsAQuietNoOp()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var vm = await LoadAsync(fixture);
        await vm.Commands!.FetchCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        // "git fetch --all" with nothing to fetch succeeds, so this must not look like a failure.
        Assert.False(vm.Commands.IsError);
        Assert.False(vm.Commands.IsNetworkOperationRunning);
    }

    [Fact]
    public async Task AFailedNetworkOperationIsReportedAsAnError()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var vm = await LoadAsync(fixture);
        await vm.Commands!.PushCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        // There is nowhere to push to, so this genuinely fails.
        Assert.True(vm.Commands.IsError);
        Assert.False(string.IsNullOrEmpty(vm.Commands.StatusText));
        Assert.False(vm.Commands.IsNetworkOperationRunning);
    }
}
