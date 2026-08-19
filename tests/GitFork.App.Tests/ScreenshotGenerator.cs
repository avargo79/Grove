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

        // GITFORK_SCREENSHOT_VIEW=working captures the staging pane instead of a commit.
        if (Environment.GetEnvironmentVariable("GITFORK_SCREENSHOT_VIEW") == "working")
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
