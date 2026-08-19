using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Grove.App.ViewModels;
using Grove.App.Views;
using Grove.Core;
using Grove.Core.Tests;

namespace Grove.App.Tests;

/// <summary>The views that close the gaps where Core had a feature and the app had no way in.</summary>
public class FileTreeAndIntegrationsTests
{
    private static async Task<GitRepository> OpenAsync(TestRepository fixture)
    {
        var repo = await GitRepository.OpenAsync(fixture.Path);
        Assert.NotNull(repo);
        return repo!;
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static TestRepository CreateNestedTree()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.WriteFile("README.md", "# hello\n");
        fixture.WriteFile("src/app/Program.cs", "public class App\n{\n}\n");
        fixture.WriteFile("src/app/Helper.cs", "// helper\n");
        fixture.WriteFile("src/lib/Core.cs", "// core\n");
        fixture.CommitAll("first");
        return fixture;
    }

    // ----------------------------------------------------------- file tree

    [Fact]
    public async Task TheFlatPathListIsTurnedIntoFolders()
    {
        using var fixture = CreateNestedTree();
        var vm = new FileTreeViewModel(await OpenAsync(fixture));

        await vm.LoadAsync(ct: TestContext.Current.CancellationToken);

        // git reports "src/app/Program.cs"; the nesting is built here.
        var src = Assert.Single(vm.Roots, n => n.Name == "src");
        Assert.True(src.IsDirectory);
        Assert.Equal(2, src.Children.Count);

        var app = Assert.Single(src.Children, n => n.Name == "app");
        Assert.Equal(2, app.Children.Count);
        Assert.Contains(app.Children, n => n.Name == "Program.cs" && !n.IsDirectory);
    }

    [Fact]
    public async Task FoldersSortBeforeFilesAtEachLevel()
    {
        using var fixture = CreateNestedTree();
        var vm = new FileTreeViewModel(await OpenAsync(fixture));

        await vm.LoadAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(vm.Roots[0].IsDirectory);
        Assert.Equal("src", vm.Roots[0].Name);
        Assert.Equal("README.md", vm.Roots[^1].Name);
    }

    [Fact]
    public async Task AFolderReportsHowManyFilesAreUnderIt()
    {
        using var fixture = CreateNestedTree();
        var vm = new FileTreeViewModel(await OpenAsync(fixture));

        await vm.LoadAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal("3 files", vm.Roots.Single(n => n.Name == "src").SizeDisplay);
    }

    [Fact]
    public async Task TheTreeReflectsTheRevisionAskedFor()
    {
        using var fixture = TestRepository.CreateEmpty();
        var first = fixture.Commit("first", "a.txt", "one\n");
        fixture.Commit("second", "b.txt", "two\n");

        var vm = new FileTreeViewModel(await OpenAsync(fixture));

        await vm.LoadAsync(first, TestContext.Current.CancellationToken);
        Assert.Single(vm.Roots);
        Assert.Contains(first[..7], vm.Title, StringComparison.Ordinal);

        await vm.LoadAsync("HEAD", TestContext.Current.CancellationToken);
        Assert.Equal(2, vm.Roots.Count);
    }

    [Fact]
    public async Task SelectingAFileShowsItsContentsWithSyntaxColouring()
    {
        using var fixture = CreateNestedTree();
        var vm = new FileTreeViewModel(await OpenAsync(fixture));
        await vm.LoadAsync(ct: TestContext.Current.CancellationToken);

        var program = vm.Roots.Single(n => n.Name == "src")
            .Children.Single(n => n.Name == "app")
            .Children.Single(n => n.Name == "Program.cs");

        vm.SelectedNode = program;
        await vm.PendingContentLoad;

        Assert.Equal(3, vm.ContentLines.Count);
        Assert.Equal(1, vm.ContentLines[0].Number);
        Assert.Contains(vm.ContentLines[0].Runs, r => r is { Text: "public", Token: TokenKind.Keyword });
    }

    [Fact]
    public async Task SelectingAFolderShowsNothingRatherThanAnError()
    {
        using var fixture = CreateNestedTree();
        var vm = new FileTreeViewModel(await OpenAsync(fixture));
        await vm.LoadAsync(ct: TestContext.Current.CancellationToken);

        vm.SelectedNode = vm.Roots.Single(n => n.Name == "src");
        await vm.PendingContentLoad;

        Assert.Empty(vm.ContentLines);
        Assert.False(vm.HasContentMessage);
    }

    [Fact]
    public async Task ABinaryFileSaysSoRatherThanShowingMojibake()
    {
        using var fixture = TestRepository.CreateEmpty();
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Path, "blob.bin"),
            [0x00, 0x01, 0x02, 0xFF, 0x00, 0x42],
            TestContext.Current.CancellationToken);
        fixture.CommitAll("add binary");

        var vm = new FileTreeViewModel(await OpenAsync(fixture));
        await vm.LoadAsync(ct: TestContext.Current.CancellationToken);

        vm.SelectedNode = vm.Roots[0];
        await vm.PendingContentLoad;

        Assert.True(vm.HasContentMessage);
        Assert.Contains("Binary file", vm.ContentMessage);
        Assert.Empty(vm.ContentLines);
    }

    [Fact]
    public async Task AnEmptyRevisionSaysSo()
    {
        using var fixture = TestRepository.CreateEmpty();
        var vm = new FileTreeViewModel(await OpenAsync(fixture));

        await vm.LoadAsync(ct: TestContext.Current.CancellationToken);

        Assert.Empty(vm.Roots);
        Assert.Equal("Nothing in this revision.", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task TheFileTreeWindowRendersTheTreeAndContents()
    {
        using var fixture = CreateNestedTree();
        var vm = new FileTreeViewModel(await OpenAsync(fixture));
        var window = new FileTreeWindow { DataContext = vm };
        window.Show();

        await vm.LoadAsync();
        window.UpdateLayout();

        Assert.NotNull(Find<TreeView>(window, "FileTree").ItemsSource);

        vm.SelectedNode = vm.Roots.Single(n => n.Name == "README.md");
        await vm.PendingContentLoad;
        window.UpdateLayout();

        Assert.True(Find<ListBox>(window, "ContentList").IsVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text),
            t => t == "README.md");
        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));
    }

    // -------------------------------------------------- submodules and LFS

    [Fact]
    public async Task ARepositoryWithNoSubmodulesSaysSo()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var vm = new IntegrationsViewModel(await OpenAsync(fixture));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.HasNoSubmodules);
        Assert.Equal("No submodules in this repository.", vm.StatusText);
    }

    [Fact]
    public async Task ASubmoduleIsListedWithItsStateAndCommit()
    {
        using var inner = TestRepository.CreateEmpty();
        inner.Commit("inner", "inner.txt", "content\n");

        using var outer = TestRepository.CreateEmpty();
        outer.Commit("outer", "outer.txt", "content\n");
        outer.Git("-c", "protocol.file.allow=always", "submodule", "add", "--quiet", inner.Path, "vendor/lib");
        outer.CommitAll("add the submodule");

        var vm = new IntegrationsViewModel(await OpenAsync(outer));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var submodule = Assert.Single(vm.Submodules);
        Assert.Equal("vendor/lib", submodule.Path);
        Assert.Equal("up to date", submodule.StateDisplay);
        Assert.False(submodule.NeedsAttention);
        Assert.Equal("1 submodule", vm.StatusText);
    }

    [Fact]
    public async Task AnUninitialisedSubmoduleIsFlaggedForAttention()
    {
        using var inner = TestRepository.CreateEmpty();
        inner.Commit("inner", "inner.txt", "content\n");

        using var outer = TestRepository.CreateEmpty();
        outer.Commit("outer", "outer.txt", "content\n");
        outer.Git("-c", "protocol.file.allow=always", "submodule", "add", "--quiet", inner.Path, "vendor/lib");
        outer.CommitAll("add the submodule");
        outer.Git("submodule", "deinit", "-f", "vendor/lib");

        var vm = new IntegrationsViewModel(await OpenAsync(outer));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var submodule = Assert.Single(vm.Submodules);
        Assert.True(submodule.NeedsAttention);
        Assert.Equal("not initialised", submodule.StateDisplay);
    }

    [Fact]
    public async Task LfsSectionsStayEmptyWhenItIsNotSetUp()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var vm = new IntegrationsViewModel(await OpenAsync(fixture));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // Whether git-lfs exists on this machine is not something a test can assume; either way
        // there is nothing tracked here.
        Assert.Empty(vm.LfsFiles);
        Assert.Empty(vm.LfsLocks);
        Assert.False(vm.HasLfsFiles);
    }

    [AvaloniaFact]
    public async Task TheIntegrationsWindowExplainsWhatIsAbsent()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var vm = new IntegrationsViewModel(await OpenAsync(fixture));
        var window = new IntegrationsWindow { DataContext = vm };
        window.Show();

        await vm.LoadAsync();
        window.UpdateLayout();

        // Empty lists would read as a failure; saying so plainly does not.
        Assert.True(Find<TextBlock>(window, "NoSubmodulesLabel").IsVisible);
        Assert.False(Find<Button>(window, "UpdateSubmodulesButton").IsVisible);
        Assert.Null(Record.Exception(() => window.CaptureRenderedFrame()?.Dispose()));
    }

    // ------------------------------------------------------------ gitflow

    private static async Task<MainViewModel> LoadFlowRepositoryAsync(TestRepository fixture)
    {
        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);
        Assert.NotNull(repository);

        repository!.Commands!.ConfirmAsync = _ => Task.FromResult(true);
        return repository;
    }

    [Fact]
    public async Task StartingAFeatureBranchesFromDevelop()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");

        var repository = await LoadFlowRepositoryAsync(fixture);
        repository.Commands!.PromptAsync = _ => Task.FromResult<string?>("login");

        await repository.Commands.StartFeatureCommand.ExecuteAsync(null);
        await repository.Commands.PendingOperation;

        Assert.Equal("feature/login", fixture.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
    }

    [Fact]
    public async Task CancellingTheNamePromptStartsNothing()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");

        var repository = await LoadFlowRepositoryAsync(fixture);
        repository.Commands!.PromptAsync = _ => Task.FromResult<string?>(null);

        await repository.Commands.StartFeatureCommand.ExecuteAsync(null);
        await repository.Commands.PendingOperation;

        Assert.Equal("main", fixture.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
    }

    [Fact]
    public async Task FinishingMergesTheBranchAndDeletesIt()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");
        fixture.Git("checkout", "--quiet", "-b", "feature/login", "develop");
        fixture.Commit("the feature work", "login.cs", "code\n");

        var repository = await LoadFlowRepositoryAsync(fixture);
        await repository.Commands!.FinishCurrentFlowBranchCommand.ExecuteAsync(null);
        await repository.Commands.PendingOperation;

        Assert.Equal("develop", fixture.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.DoesNotContain("feature/login", fixture.Git("branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinishingIsBlockedUntilConfirmed()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");
        fixture.Git("checkout", "--quiet", "-b", "feature/login", "develop");
        fixture.Commit("the feature work", "login.cs", "code\n");

        var repository = await LoadFlowRepositoryAsync(fixture);
        repository.Commands!.ConfirmAsync = _ => Task.FromResult(false);

        await repository.Commands.FinishCurrentFlowBranchCommand.ExecuteAsync(null);
        await repository.Commands.PendingOperation;

        Assert.Contains("feature/login", fixture.Git("branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinishingANonFlowBranchSaysSoRatherThanMergingIt()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");
        fixture.Git("branch", "develop");
        fixture.Git("checkout", "--quiet", "-b", "just-a-branch");

        var repository = await LoadFlowRepositoryAsync(fixture);
        await repository.Commands!.FinishCurrentFlowBranchCommand.ExecuteAsync(null);
        await repository.Commands.PendingOperation;

        Assert.True(repository.Commands.IsError);
        Assert.Contains("not a git-flow branch", repository.Commands.StatusText, StringComparison.Ordinal);
        Assert.Contains("just-a-branch", fixture.Git("branch", "--list"), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task TheRepositoryToolbarOffersTheNewViews()
    {
        using var fixture = CreateNestedTree();
        var (window, _, _) = await TestShell.OpenAsync(fixture.Path);

        Assert.True(Find<Button>(window, "FilesButton").IsEffectivelyVisible);
        Assert.True(Find<Button>(window, "IntegrationsButton").IsEffectivelyVisible);
        Assert.True(Find<Button>(window, "FlowButton").IsEffectivelyVisible);
    }
}
