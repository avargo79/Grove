using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GitGui.App.ViewModels;
using GitGui.App.Views;
using GitGui.Core;
using GitGui.Core.Tests;

namespace GitGui.App.Tests;

/// <summary>The shell: tabs, the recent list, settings, filtering, paging and the palette.</summary>
public class ShellAndSettingsTests
{
    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    // --------------------------------------------------------------- tabs

    [Fact]
    public async Task OpeningARepositoryAddsATab()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);

        Assert.NotNull(repository);
        Assert.Single(shell.Repositories);
        Assert.Same(repository, shell.SelectedRepository);
        Assert.True(shell.HasRepositories);
    }

    [Fact]
    public async Task SeveralRepositoriesCanBeOpenAtOnce()
    {
        using var one = TestRepository.CreateEmpty();
        one.Commit("first", "a.txt", "one\n");
        using var two = TestRepository.CreateEmpty();
        two.Commit("first", "b.txt", "two\n");

        var shell = TestShell.NewShell();
        await shell.OpenAsync(one.Path);
        await shell.OpenAsync(two.Path);

        Assert.Equal(2, shell.Repositories.Count);
    }

    [Fact]
    public async Task ReopeningTheSameRepositoryBringsItsTabForwardRatherThanDuplicatingIt()
    {
        using var one = TestRepository.CreateEmpty();
        one.Commit("first", "a.txt", "one\n");
        using var two = TestRepository.CreateEmpty();
        two.Commit("first", "b.txt", "two\n");

        var shell = TestShell.NewShell();
        var first = await shell.OpenAsync(one.Path);
        await shell.OpenAsync(two.Path);
        var again = await shell.OpenAsync(one.Path);

        // Two views of one repository would only invite confusion.
        Assert.Equal(2, shell.Repositories.Count);
        Assert.Same(first, again);
        Assert.Same(first, shell.SelectedRepository);
    }

    [Fact]
    public async Task OpeningSomethingThatIsNotARepositorySaysSo()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitgui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var shell = TestShell.NewShell();
            var repository = await shell.OpenAsync(path);

            Assert.Null(repository);
            Assert.Empty(shell.Repositories);
            Assert.Contains("not inside a git repository", shell.StatusMessage);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task ClosingATabSelectsANeighbourRatherThanNothing()
    {
        using var one = TestRepository.CreateEmpty();
        one.Commit("first", "a.txt", "one\n");
        using var two = TestRepository.CreateEmpty();
        two.Commit("first", "b.txt", "two\n");

        var shell = TestShell.NewShell();
        var first = await shell.OpenAsync(one.Path);
        await shell.OpenAsync(two.Path);

        shell.CloseRepositoryCommand.Execute(shell.SelectedRepository);

        Assert.Single(shell.Repositories);
        Assert.Same(first, shell.SelectedRepository);
    }

    [Fact]
    public async Task ClosingTheLastTabLeavesTheEmptyState()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var shell = TestShell.NewShell();
        await shell.OpenAsync(fixture.Path);
        shell.CloseRepositoryCommand.Execute(shell.SelectedRepository);

        Assert.Empty(shell.Repositories);
        Assert.Null(shell.SelectedRepository);
        Assert.True(shell.HasNoRepositories);
    }

    [Fact]
    public async Task ClosingATabReleasesTheRepositorysResources()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "Program.cs", "var x = 1;\n");

        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);
        await repository!.PendingDetailLoad;

        var detail = repository.Detail;
        Assert.NotNull(detail);

        shell.CloseRepositoryCommand.Execute(repository);

        // Disposing twice must stay harmless, since the shell disposes on close and again on exit.
        Assert.Null(Record.Exception(repository.Dispose));
        Assert.Null(Record.Exception(detail!.Dispose));
    }

    [Fact]
    public async Task SelectingAnotherCommitReleasesThePreviousDetail()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);
        await repository!.PendingDetailLoad;

        var first = repository.Detail;
        repository.SelectedCommit = repository.Commits[^1];
        await repository.PendingDetailLoad;

        Assert.NotSame(first, repository.Detail);
        Assert.Null(Record.Exception(first!.Dispose));
    }

    // ------------------------------------------------------- recent list

    [Fact]
    public async Task OpeningARepositoryRemembersIt()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var shell = TestShell.NewShell();
        await shell.OpenAsync(fixture.Path);

        var recent = Assert.Single(shell.Recent);
        Assert.Equal(shell.Repositories[0].RepositoryPath, recent.Path);
    }

    [Fact]
    public async Task TheRecentListSurvivesRestartingTheShell()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var settingsPath = Path.Combine(
            Path.GetTempPath(), "gitgui-tests", Guid.NewGuid().ToString("N"), "settings.json");

        var first = new ShellViewModel(new SettingsStore(settingsPath)) { WatchForChanges = false };
        await first.OpenAsync(fixture.Path);

        var second = new ShellViewModel(new SettingsStore(settingsPath)) { WatchForChanges = false };

        Assert.Single(second.Recent);
        Directory.Delete(Path.GetDirectoryName(settingsPath)!, recursive: true);
    }

    [Fact]
    public async Task ARepositoryThatFailsToOpenIsNotRemembered()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitgui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var shell = TestShell.NewShell();
            await shell.OpenAsync(path);

            Assert.Empty(shell.Recent);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task ARecentEntryCanBeRemoved()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var shell = TestShell.NewShell();
        await shell.OpenAsync(fixture.Path);
        shell.RemoveRecentCommand.Execute(shell.Recent[0]);

        Assert.Empty(shell.Recent);
    }

    // --------------------------------------------------------- filtering

    private static TestRepository CreateSearchableHistory()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("fix the login bug", "login.cs", "1\n");
        fixture.Commit("add a feature", "feature.cs", "2\n");
        fixture.Commit("fix the logout bug", "logout.cs", "3\n");
        return fixture;
    }

    [Fact]
    public async Task SearchingNarrowsTheCommitList()
    {
        using var fixture = CreateSearchableHistory();
        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);

        repository!.FilterText = "fix";
        await repository.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal(2, repository.Commits.Count);
        Assert.True(repository.IsFiltered);
        Assert.Contains("message contains", repository.FilterDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchingByPathNarrowsToThatFile()
    {
        using var fixture = CreateSearchableHistory();
        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);

        repository!.FilterPath = "login.cs";
        await repository.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Single(repository.Commits);
    }

    [Fact]
    public async Task ClearingTheFilterRestoresTheWholeHistory()
    {
        using var fixture = CreateSearchableHistory();
        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);

        repository!.FilterText = "fix";
        await repository.ApplyFilterCommand.ExecuteAsync(null);
        await repository.ClearFilterCommand.ExecuteAsync(null);

        Assert.Equal(3, repository.Commits.Count);
        Assert.False(repository.IsFiltered);
        Assert.Equal(string.Empty, repository.FilterText);
    }

    // ------------------------------------------------------------ paging

    [Fact]
    public async Task ALongHistoryIsPagedRatherThanTruncated()
    {
        using var fixture = TestRepository.CreateEmpty();
        for (var i = 1; i <= 12; i++)
            fixture.Commit($"commit {i}", "a.txt", $"{i}\n");

        var shell = TestShell.NewShell();
        shell.ApplySettings(shell.Settings with { CommitPageSize = 50 });

        var repository = await shell.OpenAsync(fixture.Path);
        repository!.ApplySettings(shell.Settings with { CommitPageSize = 5 });
        await repository.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(5, repository.Commits.Count);
        Assert.True(repository.HasMoreCommits);

        await repository.LoadMoreCommitsCommand.ExecuteAsync(null);

        Assert.Equal(10, repository.Commits.Count);
        Assert.True(repository.HasMoreCommits);
    }

    [Fact]
    public async Task LoadingMoreKeepsTheGraphConsistentAcrossPages()
    {
        using var fixture = TestRepository.CreateEmpty();
        for (var i = 1; i <= 8; i++)
            fixture.Commit($"commit {i}", "a.txt", $"{i}\n");

        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);
        repository!.ApplySettings(shell.Settings with { CommitPageSize = 4 });
        await repository.RefreshCommand.ExecuteAsync(null);
        await repository.LoadMoreCommitsCommand.ExecuteAsync(null);

        // The graph is laid out over the whole list, so widths must still agree after appending.
        Assert.Equal(8, repository.Commits.Count);
        Assert.Single(repository.Commits.Select(c => c.GraphWidth).Distinct());
        Assert.Equal(8, repository.Commits.Select(c => c.Sha).Distinct().Count());
    }

    [Fact]
    public async Task ThereIsNoMoreToLoadWhenTheHistoryFits()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("only", "a.txt", "one\n");

        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);

        Assert.False(repository!.HasMoreCommits);
    }

    // ---------------------------------------------------------- settings

    [Fact]
    public void SettingsAreEditedOnACopySoCancellingReallyCancels()
    {
        var vm = new SettingsViewModel(AppSettings.Default) { DiffContextLines = 12, IsLightTheme = true };

        vm.CancelCommand.Execute(null);

        Assert.False(vm.Accepted);
    }

    [Fact]
    public void SavingProducesTheEditedSettings()
    {
        var vm = new SettingsViewModel(AppSettings.Default)
        {
            IsLightTheme = true,
            DiffContextLines = 12,
            WhitespaceIndex = 2,
            CommitPageSize = 750,
        };

        vm.SaveCommand.Execute(null);
        var settings = vm.ToSettings();

        Assert.True(vm.Accepted);
        Assert.Equal(AppTheme.Light, settings.Theme);
        Assert.Equal(12, settings.DiffContextLines);
        Assert.Equal(WhitespaceMode.IgnoreAll, settings.DiffWhitespace);
        Assert.Equal(750, settings.CommitPageSize);
    }

    [Fact]
    public void NonsensicalValuesAreClampedRatherThanSaved()
    {
        var vm = new SettingsViewModel(AppSettings.Default) { DiffContextLines = -5, CommitPageSize = 0 };

        var settings = vm.ToSettings();

        // A page size of zero would load nothing and look like a broken repository.
        Assert.Equal(0, settings.DiffContextLines);
        Assert.Equal(50, settings.CommitPageSize);
    }

    [Fact]
    public async Task ChangedSettingsReachTheOpenRepositories()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "a.txt", "two\n");

        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);
        await repository!.PendingDetailLoad;

        shell.ApplySettings(shell.Settings with { DiffContextLines = 9, ShowSyntaxHighlighting = false });

        Assert.Equal(9, repository.Detail!.Diff.ContextLines);
        Assert.False(repository.Detail.Diff.ShowSyntaxHighlighting);
    }

    [Fact]
    public async Task ChangingTheThemeAsksTheViewToSwitchPalettes()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var shell = TestShell.NewShell();
        AppTheme? applied = null;
        shell.ApplyTheme = theme => applied = theme;

        await shell.OpenAsync(fixture.Path);
        shell.ApplySettings(shell.Settings with { Theme = AppTheme.Light });

        Assert.Equal(AppTheme.Light, applied);
    }

    [Fact]
    public void SettingsThatDidNotChangeTheThemeDoNotSwitchPalettes()
    {
        var shell = TestShell.NewShell();
        var applied = 0;
        shell.ApplyTheme = _ => applied++;

        shell.ApplySettings(shell.Settings with { DiffContextLines = 7 });

        Assert.Equal(0, applied);
    }

    // ----------------------------------------------------- command palette

    private static IReadOnlyList<PaletteCommand> SampleCommands(Action? onRun = null) =>
    [
        new("Fetch", "Remote", "Ctrl+Shift+F", () => { onRun?.Invoke(); return Task.CompletedTask; }),
        new("Push", "Remote", "Ctrl+Shift+P", () => Task.CompletedTask),
        new("New branch…", "Branch", "Ctrl+B", () => Task.CompletedTask),
    ];

    [Fact]
    public void ThePaletteListsEverythingUntilItIsFiltered()
    {
        var vm = new CommandPaletteViewModel(SampleCommands());

        Assert.Equal(3, vm.Commands.Count);

        vm.Query = "branch";

        Assert.Single(vm.Commands);
        Assert.Equal("New branch…", vm.Commands[0].Name);
    }

    [Fact]
    public void ThePaletteAlsoMatchesOnCategory()
    {
        var vm = new CommandPaletteViewModel(SampleCommands()) { Query = "remote" };

        Assert.Equal(2, vm.Commands.Count);
    }

    [Fact]
    public void ThePaletteAlwaysHasSomethingSelectedSoEnterWorks()
    {
        var vm = new CommandPaletteViewModel(SampleCommands());
        Assert.NotNull(vm.SelectedCommand);

        vm.Query = "push";
        Assert.Equal("Push", vm.SelectedCommand!.Name);
    }

    [Fact]
    public void AQueryThatMatchesNothingSaysSo()
    {
        var vm = new CommandPaletteViewModel(SampleCommands()) { Query = "nothing matches this" };

        Assert.False(vm.HasResults);
        Assert.Null(vm.SelectedCommand);
    }

    [Fact]
    public async Task InvokingRunsTheCommandAndClosesThePalette()
    {
        var ran = false;
        var vm = new CommandPaletteViewModel(SampleCommands(() => ran = true));
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.InvokeCommand.Execute(vm.Commands[0]);
        await vm.PendingInvocation;

        Assert.True(ran);
        Assert.True(closed);
    }

    // ------------------------------------------------------------ render

    [AvaloniaFact]
    public void TheEmptyShellExplainsWhatToDo()
    {
        var (window, _) = TestShell.Empty();

        Assert.True(Find<StackPanel>(window, "EmptyState").IsVisible);
        Assert.False(Find<TabControl>(window, "RepositoryTabs").IsVisible);
        Assert.True(Find<Button>(window, "OpenRepositoryButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task AnOpenRepositoryFillsTheTabAndHidesTheEmptyState()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var (window, _, _) = await TestShell.OpenAsync(fixture.Path);

        Assert.False(Find<StackPanel>(window, "EmptyState").IsVisible);
        Assert.True(Find<TabControl>(window, "RepositoryTabs").IsVisible);
        Assert.Single(window.GetVisualDescendants().OfType<RepositoryView>());
    }

    [AvaloniaFact]
    public async Task TheFilterBarIsBoundToTheRepository()
    {
        using var fixture = CreateSearchableHistory();
        var (window, _, repository) = await TestShell.OpenAsync(fixture.Path);

        Find<TextBox>(window, "SearchMessage").Text = "fix";
        window.UpdateLayout();

        Assert.Equal("fix", repository.FilterText);
        Assert.False(Find<Button>(window, "ClearSearchButton").IsVisible);

        await repository.ApplyFilterCommand.ExecuteAsync(null);
        window.UpdateLayout();

        Assert.True(Find<Button>(window, "ClearSearchButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task TheLoadMoreRowAppearsOnlyWhenThereIsMore()
    {
        using var fixture = TestRepository.CreateEmpty();
        for (var i = 1; i <= 12; i++)
            fixture.Commit($"commit {i}", "a.txt", $"{i}\n");

        var (window, shell, repository) = await TestShell.OpenAsync(fixture.Path);

        Assert.False(Find<Border>(window, "LoadMoreRow").IsVisible);

        repository.ApplySettings(shell.Settings with { CommitPageSize = 5 });
        await repository.RefreshCommand.ExecuteAsync(null);
        window.UpdateLayout();

        Assert.True(Find<Border>(window, "LoadMoreRow").IsVisible);
    }

    [AvaloniaFact]
    public async Task SwitchingToTheLightThemeRepaintsTheWindow()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var (window, _, _) = await TestShell.OpenAsync(fixture.Path);

        static double Brightness(Avalonia.Media.Imaging.WriteableBitmap frame)
        {
            using var buffer = frame.Lock();
            double total = 0;
            var count = 0;

            unsafe
            {
                var scan0 = (byte*)buffer.Address;
                for (var y = 0; y < buffer.Size.Height; y += 4)
                {
                    var row = scan0 + (y * buffer.RowBytes);
                    for (var x = 0; x < buffer.Size.Width; x += 4)
                    {
                        var p = row + (x * 4);
                        total += (p[0] + p[1] + p[2]) / 3.0;
                        count++;
                    }
                }
            }

            return total / count;
        }

        double dark;
        using (var frame = window.CaptureRenderedFrame()!)
            dark = Brightness(frame);

        App.ApplyTheme(AppTheme.Light);
        window.UpdateLayout();

        using var light = window.CaptureRenderedFrame()!;

        // Only the pixels can say the palette really switched; the brushes are resolved dynamically.
        Assert.True(Brightness(light) > dark + 60,
            $"the light theme did not take effect: dark={dark:F0} light={Brightness(light):F0}");

        App.ApplyTheme(AppTheme.Dark);
    }
}
