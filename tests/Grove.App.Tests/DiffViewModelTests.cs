using Grove.App.ViewModels;
using Grove.Core;
using Grove.Core.Tests;

namespace Grove.App.Tests;

/// <summary>Covers the diff pane's two layouts, its options, and the runs it builds.</summary>
public class DiffViewModelTests
{
    private static FileDiff Parse(string patch) => DiffParser.ParseFiles(patch)[0];

    private const string ReplacementPatch =
        """
        diff --git a/Program.cs b/Program.cs
        --- a/Program.cs
        +++ b/Program.cs
        @@ -1,3 +1,3 @@
         public class App
        -    var total = 1;
        +    var count = 1;
         }
        """;

    private static string ChangedText(IReadOnlyList<DiffRun> runs) =>
        string.Concat(runs.Where(r => r.IsWordChanged).Select(r => r.Text));

    // ------------------------------------------------------------- unified

    [Fact]
    public void LoadingBuildsBothLayoutsAtOnce()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        // Toggling the mode must not need a round trip to git.
        Assert.NotEmpty(vm.UnifiedRows);
        Assert.NotEmpty(vm.SideBySideRows);
    }

    [Fact]
    public void TheUnifiedViewStartsEachHunkWithItsHeader()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        Assert.True(vm.UnifiedRows[0].IsHunkHeader);
        Assert.StartsWith("@@", vm.UnifiedRows[0].Text);
    }

    [Fact]
    public void UnifiedLinesCarryTheirNumbersAndMarkers()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        var added = vm.UnifiedRows.Single(r => r.IsAdded);
        var removed = vm.UnifiedRows.Single(r => r.IsRemoved);

        Assert.Equal("+", added.Marker);
        Assert.Equal("-", removed.Marker);
        Assert.Equal(2, added.NewLineNumber);
        Assert.Equal(2, removed.OldLineNumber);
    }

    [Fact]
    public void BothSidesOfAReplacementGetWordLevelRuns()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        Assert.Equal("total", ChangedText(vm.UnifiedRows.Single(r => r.IsRemoved).Runs));
        Assert.Equal("count", ChangedText(vm.UnifiedRows.Single(r => r.IsAdded).Runs));
    }

    [Fact]
    public void ContextLinesAreNotMarkedAsChanged()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        Assert.All(vm.UnifiedRows.Where(r => r.Kind == DiffLineKind.Context),
            r => Assert.Empty(ChangedText(r.Runs)));
    }

    [Fact]
    public void SyntaxColouringIsAppliedFromTheFilesExtension()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        var added = vm.UnifiedRows.Single(r => r.IsAdded);
        Assert.Contains(added.Runs, r => r is { Text: "var", Token: TokenKind.Keyword });
    }

    [Fact]
    public void TurningWordHighlightingOffLeavesNothingMarked()
    {
        var vm = new DiffViewModel { ShowWordHighlighting = false };
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        Assert.All(vm.UnifiedRows, r => Assert.Empty(ChangedText(r.Runs)));
    }

    // -------------------------------------------------------- side by side

    [Fact]
    public void SideBySidePairsAReplacementOntoOneRow()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        var row = vm.SideBySideRows.Single(r => r.Left.IsRemoved);

        Assert.True(row.Right.IsAdded);
        Assert.Equal("    var total = 1;", row.Left.Text);
        Assert.Equal("    var count = 1;", row.Right.Text);
    }

    [Fact]
    public void SideBySideCellsCarryTheirOwnLineNumbers()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        var context = vm.SideBySideRows.First(r => r.Left is { IsEmpty: false, IsRemoved: false });

        Assert.Equal("    1", context.Left.Number);
        Assert.Equal("    1", context.Right.Number);
    }

    [Fact]
    public void SideBySideMarksOnlyTheChangedWords()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        var row = vm.SideBySideRows.Single(r => r.Left.IsRemoved);

        Assert.Equal("total", ChangedText(row.Left.Runs));
        Assert.Equal("count", ChangedText(row.Right.Runs));
    }

    [Fact]
    public void AnInsertionLeavesTheLeftCellEmpty()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,2 @@
             kept
            +added
            """), "a.txt");

        var row = vm.SideBySideRows.Single(r => r.Right.IsAdded);
        Assert.True(row.Left.IsEmpty);
        Assert.Equal("     ", row.Left.Number);
    }

    // ------------------------------------------------------------- modes

    [Fact]
    public void TheModeFlagsFollowTheSelectedMode()
    {
        var vm = new DiffViewModel();

        Assert.True(vm.IsUnified);
        Assert.False(vm.IsSideBySide);

        vm.Mode = DiffViewMode.SideBySide;

        Assert.False(vm.IsUnified);
        Assert.True(vm.IsSideBySide);
    }

    [Fact]
    public void ChangingTheModeDoesNotAskGitForAnythingNew()
    {
        var vm = new DiffViewModel();
        var reloads = 0;
        vm.OptionsChanged += (_, _) => reloads++;

        vm.Mode = DiffViewMode.SideBySide;

        // Both layouts are already built, so switching is free.
        Assert.Equal(0, reloads);
    }

    [Theory]
    [InlineData(nameof(DiffViewModel.ContextLines))]
    [InlineData(nameof(DiffViewModel.Whitespace))]
    [InlineData(nameof(DiffViewModel.ShowWordHighlighting))]
    public void OptionsThatChangeWhatGitIsAskedForRequestAReload(string property)
    {
        var vm = new DiffViewModel();
        var reloads = 0;
        vm.OptionsChanged += (_, _) => reloads++;

        switch (property)
        {
            case nameof(DiffViewModel.ContextLines):
                vm.ContextLines = 10;
                break;
            case nameof(DiffViewModel.Whitespace):
                vm.Whitespace = WhitespaceMode.IgnoreAll;
                break;
            default:
                vm.ShowWordHighlighting = false;
                break;
        }

        Assert.Equal(1, reloads);
    }

    [Fact]
    public void SyntaxHighlightingIsPurelyPresentationAndNeedsNoReload()
    {
        var vm = new DiffViewModel();
        var reloads = 0;
        vm.OptionsChanged += (_, _) => reloads++;

        vm.ShowSyntaxHighlighting = false;

        Assert.Equal(0, reloads);
    }

    [Fact]
    public void OptionsAreHandedToGitAsTheyAreSet()
    {
        var vm = new DiffViewModel { ContextLines = 9, Whitespace = WhitespaceMode.IgnoreChange };

        var args = vm.Options.ToArguments().ToList();

        Assert.Contains("--unified=9", args);
        Assert.Contains("--ignore-space-change", args);
    }

    // ------------------------------------------------------- empty states

    [Fact]
    public void ADiffThatCouldNotBeReadSaysSo()
    {
        var vm = new DiffViewModel();
        vm.Load(null, "a.txt");

        Assert.True(vm.HasEmptyMessage);
        Assert.Contains("could not be read", vm.EmptyMessage);
    }

    [Fact]
    public void AFileWithNoChangesSaysSo()
    {
        var vm = new DiffViewModel();
        vm.Load(new FileDiff { HeaderLines = [], Hunks = [], Path = "a.txt" }, "a.txt");

        Assert.Equal("No changes in this file.", vm.EmptyMessage);
    }

    [Fact]
    public void WithWhitespaceIgnoredAnEmptyDiffExplainsWhy()
    {
        var vm = new DiffViewModel { Whitespace = WhitespaceMode.IgnoreAll };
        vm.Load(new FileDiff { HeaderLines = [], Hunks = [], Path = "a.txt" }, "a.txt");

        // Otherwise "no changes" looks like the option did nothing.
        Assert.Equal("No changes once whitespace is ignored.", vm.EmptyMessage);
    }

    [Fact]
    public void ABinaryFileSaysSoRatherThanShowingNothing()
    {
        var vm = new DiffViewModel();
        vm.Load(new FileDiff { HeaderLines = [], Hunks = [], Path = "a.bin", IsBinary = true }, "a.bin");

        Assert.Contains("Binary file", vm.EmptyMessage);
    }

    [Fact]
    public void LoadingANewDiffClearsTheEmptyMessage()
    {
        var vm = new DiffViewModel();
        vm.Load(null, "a.txt");
        Assert.True(vm.HasEmptyMessage);

        vm.Load(Parse(ReplacementPatch), "Program.cs");

        Assert.False(vm.HasEmptyMessage);
        Assert.NotEmpty(vm.UnifiedRows);
    }

    // -------------------------------------------------------------- image

    [Fact]
    public void AnImageDiffReplacesTheTextRows()
    {
        var vm = new DiffViewModel();
        vm.Load(Parse(ReplacementPatch), "Program.cs");

        vm.LoadImage(ImageDiffViewModel.Create(null, null));

        Assert.True(vm.HasImage);
        Assert.Empty(vm.UnifiedRows);
        Assert.Empty(vm.SideBySideRows);
    }

    [Fact]
    public void UnreadableImageBytesSaySoRatherThanLeavingABlankPane()
    {
        var image = ImageDiffViewModel.Create([0x01, 0x02, 0x03], null);

        Assert.False(image.HasBefore);
        Assert.Equal("3 bytes — not a readable image", image.BeforeCaption);
    }

    [Fact]
    public void AMissingSideIsLabelledByWhyItIsMissing()
    {
        var added = ImageDiffViewModel.Create(null, [0x01]);
        var deleted = ImageDiffViewModel.Create([0x01], null);

        Assert.Equal("Not in this revision", added.BeforeCaption);
        Assert.Equal("Deleted", deleted.AfterCaption);
    }

    [Fact]
    public void ReplacingAnImageDiffReleasesTheOneItReplaces()
    {
        var vm = new DiffViewModel();
        var first = ImageDiffViewModel.Create([0x01], null);
        vm.LoadImage(first);

        vm.LoadImage(ImageDiffViewModel.Create([0x02], null));

        // Decoded bitmaps are native memory; clicking through image commits would accumulate them.
        Assert.True(first.IsDisposed);
    }

    [Fact]
    public void LoadingATextDiffReleasesAnyImageItReplaces()
    {
        var vm = new DiffViewModel();
        var image = ImageDiffViewModel.Create([0x01], null);
        vm.LoadImage(image);

        vm.Load(Parse(ReplacementPatch), "Program.cs");

        Assert.True(image.IsDisposed);
        Assert.False(vm.HasImage);
    }

    [Fact]
    public void DisposingThePaneReleasesItsImage()
    {
        var vm = new DiffViewModel();
        var image = ImageDiffViewModel.Create([0x01], null);
        vm.LoadImage(image);

        vm.Dispose();

        Assert.True(image.IsDisposed);
    }

    [Fact]
    public void DisposingAnImageTwiceIsHarmless()
    {
        var image = ImageDiffViewModel.Create([0x01], null);
        image.Dispose();

        Assert.Null(Record.Exception(image.Dispose));
    }

    // ------------------------------------------------- against a real repo

    [Fact]
    public async Task TheCommitDetailPaneUsesTheNewDiffView()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "Program.cs", "var total = 1;\n");
        fixture.Commit("second", "Program.cs", "var count = 1;\n");

        var vm = new MainViewModel { WatchForChanges = false };
        await vm.LoadRepositoryAsync(fixture.Path);
        await vm.PendingDetailLoad;
        await vm.PendingDiffLoad;

        var diff = vm.Detail!.Diff;
        Assert.NotEmpty(diff.UnifiedRows);
        Assert.Equal("total", ChangedText(diff.UnifiedRows.Single(r => r.IsRemoved).Runs));
        Assert.Equal("count", ChangedText(diff.UnifiedRows.Single(r => r.IsAdded).Runs));
    }

    [Fact]
    public async Task ChangingTheContextOptionRereadsTheDiff()
    {
        using var fixture = TestRepository.CreateEmpty();
        var original = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\n";
        fixture.Commit("first", "a.txt", original);
        fixture.Commit("second", "a.txt", original.Replace("line 15\n", "CHANGED\n", StringComparison.Ordinal));

        var vm = new MainViewModel { WatchForChanges = false };
        await vm.LoadRepositoryAsync(fixture.Path);
        await vm.PendingDetailLoad;
        await vm.PendingDiffLoad;

        var before = vm.Detail!.Diff.UnifiedRows.Count;

        vm.Detail.Diff.ContextLines = 10;
        await vm.Detail.PendingDiffLoad;

        Assert.True(vm.Detail.Diff.UnifiedRows.Count > before);
    }

    [Fact]
    public async Task AnImageFileInACommitShowsAsAnImageDiff()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "readme.md", "text\n");

        // A one-pixel PNG, so the decoder has something real to work with.
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        ];
        await File.WriteAllBytesAsync(Path.Combine(fixture.Path, "logo.png"), png, TestContext.Current.CancellationToken);
        fixture.CommitAll("add an image");

        var vm = new MainViewModel { WatchForChanges = false };
        await vm.LoadRepositoryAsync(fixture.Path);
        await vm.PendingDetailLoad;
        await vm.PendingDiffLoad;

        Assert.True(vm.Detail!.Diff.HasImage);
        Assert.Empty(vm.Detail.Diff.UnifiedRows);
    }
}
