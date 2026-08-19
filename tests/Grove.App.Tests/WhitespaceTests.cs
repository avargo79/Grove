using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Grove.App.ViewModels;
using Grove.Core;
using Grove.Core.Tests;
using Xunit;

namespace Grove.App.Tests;

/// <summary>
/// Whitespace handling across both diff panes: that it reaches git, that the control reflects
/// what is in effect, that it survives changing commit, and that it disables the staging paths
/// it would otherwise corrupt.
/// </summary>
public class WhitespaceTests
{
    /// <summary>A commit whose only change is indentation, which is the case the flag exists for.</summary>
    private static TestRepository CreateReindentedRepository()
    {
        var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "code.txt", "if (x) {\ncall();\n}\n");
        fixture.Commit("reindent", "code.txt", "if (x) {\n    call();\n}\n");
        return fixture;
    }

    [AvaloniaFact]
    public async Task IgnoringWhitespaceEmptiesADiffThatIsOnlyIndentation()
    {
        using var fixture = CreateReindentedRepository();
        var (_, _, viewModel) = await TestShell.OpenAsync(fixture.Path);
        await viewModel.PendingDetailLoad;

        var diff = viewModel.Detail!.Diff;
        await viewModel.Detail.PendingDiffLoad;
        Assert.NotEmpty(diff.UnifiedRows);

        diff.Whitespace = WhitespaceMode.IgnoreAll;
        await viewModel.Detail.PendingDiffLoad;

        // Nothing but indentation changed, so ignoring it leaves git with nothing to report.
        Assert.Empty(diff.UnifiedRows.Where(r => r.IsAdded || r.IsRemoved));
    }

    [AvaloniaFact]
    public async Task TheDropdownShowsTheModeActuallyInEffect()
    {
        using var fixture = CreateReindentedRepository();
        var (window, _, viewModel) = await TestShell.OpenAsync(fixture.Path);
        await viewModel.PendingDetailLoad;

        viewModel.Detail!.Diff.Whitespace = WhitespaceMode.IgnoreChange;
        window.UpdateLayout();

        var box = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Name == "WhitespaceSelector");

        // It used to be pinned to index 0, which claimed "Show whitespace changes" over a diff
        // that was ignoring it.
        Assert.Equal((int)WhitespaceMode.IgnoreChange, box.SelectedIndex);
    }

    [AvaloniaFact]
    public async Task TheModeSurvivesSelectingAnotherCommit()
    {
        using var fixture = CreateReindentedRepository();
        fixture.Commit("third", "code.txt", "if (x) {\n    call();\n    again();\n}\n");

        var (_, _, viewModel) = await TestShell.OpenAsync(fixture.Path);
        await viewModel.PendingDetailLoad;

        viewModel.Detail!.Diff.Whitespace = WhitespaceMode.IgnoreAll;

        viewModel.SelectedCommit = viewModel.Commits[1];
        await viewModel.PendingDetailLoad;

        // A fresh detail view model is built per commit; the choice belongs to the user, not to
        // whichever pane happened to be open when they made it.
        Assert.Equal(WhitespaceMode.IgnoreAll, viewModel.Detail!.Diff.Whitespace);
    }

    [AvaloniaFact]
    public async Task TheStagingPaneAppliesTheModeToItsOwnDiff()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "code.txt", "if (x) {\ncall();\n}\n");
        fixture.WriteFile("code.txt", "if (x) {\n    call();\n}\n");

        var (_, _, viewModel) = await TestShell.OpenAsync(fixture.Path);
        viewModel.SelectWorkingCopy();

        var working = viewModel.WorkingCopy!;
        working.SelectedFile = working.UnstagedFiles.Single();
        await working.PendingDiffLoad;
        Assert.Contains(working.DiffRows, r => r.IsAdded || r.IsRemoved);

        working.Whitespace = WhitespaceMode.IgnoreAll;
        await working.PendingDiffLoad;

        Assert.DoesNotContain(working.DiffRows, r => r.IsAdded || r.IsRemoved);
    }

    [AvaloniaFact]
    public async Task IgnoringWhitespaceTurnsOffHunkAndLineStaging()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "code.txt", "one\n");
        fixture.WriteFile("code.txt", "one\ntwo\n");

        var (_, _, viewModel) = await TestShell.OpenAsync(fixture.Path);
        viewModel.SelectWorkingCopy();

        var working = viewModel.WorkingCopy!;
        working.SelectedFile = working.UnstagedFiles.Single();
        await working.PendingDiffLoad;

        Assert.True(working.StageHunkCommand.CanExecute(null));

        working.Whitespace = WhitespaceMode.IgnoreAll;
        await working.PendingDiffLoad;

        // A patch built from such a diff does not describe the bytes on disk, so applying it
        // either fails or stages the wrong thing. Whole-file staging is unaffected.
        Assert.False(working.StageHunkCommand.CanExecute(null));
        Assert.False(working.StageSelectedLinesCommand.CanExecute(null));
        Assert.NotNull(working.PartialStagingHint);
        Assert.True(working.StageFileCommand.CanExecute(working.SelectedFile));
    }

    [AvaloniaFact]
    public async Task ApplyingSettingsDoesNotEchoBackThroughTheOptionsHandler()
    {
        using var fixture = CreateReindentedRepository();
        var shell = TestShell.NewShell();
        var repository = await shell.OpenAsync(fixture.Path);
        await repository!.PendingDetailLoad;

        shell.ApplySettings(shell.Settings with
        {
            DiffContextLines = 9,
            DiffWhitespace = WhitespaceMode.IgnoreAll,
            ShowSyntaxHighlighting = false,
        });

        // Each property set raises OptionsChanged, and the handler that remembers the user's
        // choice used to capture the pane mid-update — so whichever settings were applied last
        // were overwritten by the pane's own half-updated state.
        var diff = repository.Detail!.Diff;
        Assert.Equal(9, diff.ContextLines);
        Assert.Equal(WhitespaceMode.IgnoreAll, diff.Whitespace);
        Assert.False(diff.ShowSyntaxHighlighting);
    }
}
