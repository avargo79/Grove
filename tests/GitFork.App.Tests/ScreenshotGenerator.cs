using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using GitFork.App.ViewModels;
using GitFork.App.Views;

namespace GitFork.App.Tests;

/// <summary>
/// Renders the window headlessly and writes a PNG, for the README and for eyeballing a change
/// without launching the app. Opt-in so an ordinary test run never writes to the working tree:
///
///     GITFORK_SCREENSHOT=docs/screenshot.png GITFORK_SCREENSHOT_REPO=/path/to/repo dotnet test
/// </summary>
public class ScreenshotGenerator
{
    [AvaloniaFact]
    public async Task WriteScreenshot()
    {
        var output = Environment.GetEnvironmentVariable("GITFORK_SCREENSHOT");
        if (string.IsNullOrWhiteSpace(output))
            return;

        var repositoryPath = Environment.GetEnvironmentVariable("GITFORK_SCREENSHOT_REPO")
                             ?? Directory.GetCurrentDirectory();

        var viewModel = new MainViewModel { WatchForChanges = false };
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1400,
            Height = 900,
        };
        window.Show();

        await viewModel.LoadRepositoryAsync(repositoryPath);
        await viewModel.PendingDetailLoad;
        await viewModel.PendingDiffLoad;

        var view = Environment.GetEnvironmentVariable("GITFORK_SCREENSHOT_VIEW");

        // Which pane to capture: the staging view, or the two-column diff.
        if (view == "working")
            viewModel.SelectWorkingCopy();
        else if (view == "sidebyside" && viewModel.Detail is { } detail)
            detail.Diff.Mode = DiffViewMode.SideBySide;

        window.UpdateLayout();

        using var frame = window.CaptureRenderedFrame()
                          ?? throw new InvalidOperationException("nothing was rendered");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var stream = File.Create(output);
        frame.Save(stream, new PngBitmapEncoderOptions());

        Assert.True(new FileInfo(output).Length > 0);
    }

    /// <summary>
    /// Renders the conflict banner. Builds its own throwaway repository rather than conflicting
    /// anything in a real one:
    ///
    ///     GITFORK_SCREENSHOT_CONFLICT=docs/screenshot-conflict.png dotnet test tests/GitFork.App.Tests
    /// </summary>
    [AvaloniaFact]
    public async Task WriteConflictScreenshot()
    {
        var output = Environment.GetEnvironmentVariable("GITFORK_SCREENSHOT_CONFLICT");
        if (string.IsNullOrWhiteSpace(output))
            return;

        using var fixture = GitFork.Core.Tests.TestRepository.CreateEmpty();
        fixture.Commit("Add the shared configuration file", "config.yaml", "timeout: 30\nretries: 3\n");
        fixture.Git("checkout", "--quiet", "-b", "feature/raise-timeout");
        fixture.Commit("Raise the timeout for slow networks", "config.yaml", "timeout: 120\nretries: 3\n");
        fixture.Git("checkout", "--quiet", "main");
        fixture.Commit("Lower the timeout to fail faster", "config.yaml", "timeout: 5\nretries: 3\n");

        var viewModel = new MainViewModel { WatchForChanges = false };
        var window = new MainWindow { DataContext = viewModel, Width = 1400, Height = 900 };
        window.Show();

        await viewModel.LoadRepositoryAsync(fixture.Path);
        await viewModel.PendingDetailLoad;

        viewModel.Commands!.ConfirmAsync = _ => Task.FromResult(true);
        var feature = viewModel.Sections.Single(s => s.Title == "Branches")
            .Items.Single(i => i.DisplayName == "feature/raise-timeout");

        await viewModel.Commands.MergeRefCommand.ExecuteAsync(feature);
        await viewModel.Commands.PendingOperation;

        viewModel.SelectWorkingCopy();
        window.UpdateLayout();

        using var frame = window.CaptureRenderedFrame()
                          ?? throw new InvalidOperationException("nothing was rendered");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var stream = File.Create(output);
        frame.Save(stream, new PngBitmapEncoderOptions());

        Assert.True(new FileInfo(output).Length > 0);
    }
}
