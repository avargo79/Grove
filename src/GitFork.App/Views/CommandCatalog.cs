using GitFork.App.ViewModels;

namespace GitFork.App.Views;

/// <summary>
/// The single list of what the app can do, used by both the command palette and the keyboard
/// shortcuts — so a binding and its palette entry can never disagree about what a key does.
/// </summary>
public static class CommandCatalog
{
    public static IReadOnlyList<PaletteCommand> Build(ShellViewModel shell, MainWindow window)
    {
        var commands = new List<PaletteCommand>
        {
            new("Open repository…", "Repository", "Ctrl+O", () => shell.OpenRepositoryCommand.ExecuteAsync(null)),
            new("Settings…", "Application", "Ctrl+,", () => { window.OpenSettings(); return Task.CompletedTask; }),
        };

        if (shell.SelectedRepository is not { } repository)
            return commands;

        commands.AddRange(
        [
            new("Refresh", "Repository", "F5", () => repository.RefreshCommand.ExecuteAsync(null)),
            new("Close repository", "Repository", "Ctrl+W",
                () => { shell.CloseRepositoryCommand.Execute(repository); return Task.CompletedTask; }),
            new("Fetch", "Remote", "Ctrl+Shift+F", () => Run(repository, r => r.FetchCommand.ExecuteAsync(null))),
            new("Pull", "Remote", "Ctrl+Shift+L", () => Run(repository, r => r.PullCommand.ExecuteAsync(null))),
            new("Pull (rebase)", "Remote", string.Empty,
                () => Run(repository, r => r.PullRebaseCommand.ExecuteAsync(null))),
            new("Push", "Remote", "Ctrl+Shift+P", () => Run(repository, r => r.PushCommand.ExecuteAsync(null))),
            new("Force push (with lease)…", "Remote", string.Empty,
                () => Run(repository, r => r.ForcePushCommand.ExecuteAsync(null))),
            new("Stash changes…", "Working copy", "Ctrl+Shift+S",
                () => Run(repository, r => r.StashPushCommand.ExecuteAsync(null))),
            new("Show uncommitted changes", "Working copy", "Ctrl+G",
                () => { repository.SelectWorkingCopy(); return Task.CompletedTask; }),
            new("New branch…", "Branch", "Ctrl+B",
                () => Run(repository, r => r.CreateBranchCommand.ExecuteAsync(null))),
            new("New tag…", "Branch", string.Empty,
                () => Run(repository, r => r.CreateTagCommand.ExecuteAsync(null))),
            new("Reflog…", "History", "Ctrl+R", () => window.OpenReflogAsync(repository)),
            new("Browse files…", "Repository", string.Empty,
                () => window.OpenFileTreeAsync(repository, "HEAD")),
            new("Submodules and LFS…", "Repository", string.Empty,
                () => window.OpenIntegrationsAsync(repository)),
            new("Start a feature…", "Git-flow", string.Empty,
                () => Run(repository, r => r.StartFeatureCommand.ExecuteAsync(null))),
            new("Start a release…", "Git-flow", string.Empty,
                () => Run(repository, r => r.StartReleaseCommand.ExecuteAsync(null))),
            new("Start a hotfix…", "Git-flow", string.Empty,
                () => Run(repository, r => r.StartHotfixCommand.ExecuteAsync(null))),
            new("Finish this git-flow branch…", "Git-flow", string.Empty,
                () => Run(repository, r => r.FinishCurrentFlowBranchCommand.ExecuteAsync(null))),
            new("Clear history filter", "History", "Esc",
                () => repository.ClearFilterCommand.ExecuteAsync(null)),
            new("Load more commits", "History", string.Empty,
                () => repository.LoadMoreCommitsCommand.ExecuteAsync(null)),
            new("Continue merge or rebase", "History", string.Empty,
                () => Run(repository, r => r.ContinueOperationCommand.ExecuteAsync(null))),
            new("Abort merge or rebase…", "History", string.Empty,
                () => Run(repository, r => r.AbortOperationCommand.ExecuteAsync(null))),
        ]);

        return commands;
    }

    /// <summary>Runs a repository-level command, quietly doing nothing if none is loaded yet.</summary>
    private static Task Run(MainViewModel repository, Func<RepositoryCommandsViewModel, Task> action) =>
        repository.Commands is { } commands ? action(commands) : Task.CompletedTask;
}
