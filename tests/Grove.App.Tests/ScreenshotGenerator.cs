using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Grove.App.ViewModels;
using Grove.App.Views;

namespace Grove.App.Tests;

/// <summary>
/// Renders the window headlessly and writes a PNG, for the README and for eyeballing a change
/// without launching the app. Opt-in so an ordinary test run never writes to the working tree:
///
///     GROVE_SCREENSHOT=docs/screenshot.png GROVE_SCREENSHOT_REPO=/path/to/repo dotnet test
/// </summary>
public class ScreenshotGenerator
{
    [AvaloniaFact]
    public async Task WriteScreenshot()
    {
        var output = Environment.GetEnvironmentVariable("GROVE_SCREENSHOT");
        if (string.IsNullOrWhiteSpace(output))
            return;

        var repositoryPath = Environment.GetEnvironmentVariable("GROVE_SCREENSHOT_REPO")
                             ?? Directory.GetCurrentDirectory();

        // GROVE_SCREENSHOT_THEME=light captures the light palette.
        if (Environment.GetEnvironmentVariable("GROVE_SCREENSHOT_THEME") == "light")
            App.ApplyTheme(Grove.Core.AppTheme.Light);

        var (window, _, viewModel) = await TestShell.OpenAsync(repositoryPath);
        window.Width = 1400;
        window.Height = 900;
        window.UpdateLayout();

        var view = Environment.GetEnvironmentVariable("GROVE_SCREENSHOT_VIEW");

        // Which pane to capture: the staging view, or the two-column diff. An unrecognised name
        // is an error rather than a silent fall-back, or a typo quietly writes the default view
        // over the file it was meant to replace.
        switch (view)
        {
            case null or "":
                break;
            case "working":
                viewModel.SelectWorkingCopy();
                break;
            case "sidebyside" when viewModel.Detail is { } detail:
                detail.Diff.Mode = DiffViewMode.SideBySide;
                break;
            case "sidebyside":
                break;
            default:
                throw new InvalidOperationException(
                    $"GROVE_SCREENSHOT_VIEW={view} is not a view; use 'working' or 'sidebyside'.");
        }

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
    ///     GROVE_SCREENSHOT_CONFLICT=docs/screenshot-conflict.png dotnet test tests/Grove.App.Tests
    /// </summary>
    [AvaloniaFact]
    public async Task WriteConflictScreenshot()
    {
        var output = Environment.GetEnvironmentVariable("GROVE_SCREENSHOT_CONFLICT");
        if (string.IsNullOrWhiteSpace(output))
            return;

        using var fixture = Grove.Core.Tests.TestRepository.CreateEmpty();
        fixture.Commit("Add the shared configuration file", "config.yaml", "timeout: 30\nretries: 3\n");
        fixture.Git("checkout", "--quiet", "-b", "feature/raise-timeout");
        fixture.Commit("Raise the timeout for slow networks", "config.yaml", "timeout: 120\nretries: 3\n");
        fixture.Git("checkout", "--quiet", "main");
        fixture.Commit("Lower the timeout to fail faster", "config.yaml", "timeout: 5\nretries: 3\n");

        var (window, _, viewModel) = await TestShell.OpenAsync(fixture.Path);
        window.Width = 1400;
        window.Height = 900;

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
