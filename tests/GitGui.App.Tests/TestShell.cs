using Avalonia.Controls;
using GitGui.App.ViewModels;
using GitGui.App.Views;
using GitGui.Core;

namespace GitGui.App.Tests;

/// <summary>
/// Builds a window around a single repository. The app is a tab shell, so every render test needs
/// one; this keeps that detail in one place rather than in every test.
/// </summary>
internal static class TestShell
{
    /// <summary>A shell that never writes to the user's real settings file.</summary>
    public static ShellViewModel NewShell() => new(new SettingsStore(TempSettingsPath()))
    {
        WatchForChanges = false,
    };

    private static string TempSettingsPath() => Path.Combine(
        Path.GetTempPath(), "gitgui-tests", Guid.NewGuid().ToString("N"), "settings.json");

    /// <summary>Opens a repository in a shown window and waits for its first load.</summary>
    public static async Task<(Window Window, ShellViewModel Shell, MainViewModel Repository)> OpenAsync(
        string repositoryPath)
    {
        var shell = NewShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var repository = await shell.OpenAsync(repositoryPath);
        Assert.NotNull(repository);

        await repository!.PendingDetailLoad;
        await repository.PendingDiffLoad;

        // The tab's content is only built once the tab strip itself has been laid out, so the
        // repository view does not exist after a single pass.
        window.UpdateLayout();
        window.UpdateLayout();

        return (window, shell, repository);
    }

    /// <summary>A shown window with nothing open, for empty-state assertions.</summary>
    public static (Window Window, ShellViewModel Shell) Empty()
    {
        var shell = NewShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();
        return (window, shell);
    }
}
