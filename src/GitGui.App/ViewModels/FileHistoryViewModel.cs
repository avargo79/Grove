using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitGui.Core;

namespace GitGui.App.ViewModels;

/// <summary>One commit in a file's history, with the diff it made to that file.</summary>
public sealed class FileHistoryEntryViewModel(Commit commit)
{
    public Commit Commit { get; } = commit;
    public string Sha => Commit.Sha;
    public string ShortSha => Commit.ShortSha;
    public string Subject => Commit.Subject;
    public string AuthorName => Commit.AuthorName;
    public string DateDisplay => RelativeTime.Format(Commit.AuthorDate);
    public string DateTooltip => Commit.AuthorDate.ToLocalTime()
        .ToString("f", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>
/// The history of a single path, following it through renames, with the diff for whichever commit
/// is selected.
/// </summary>
public sealed partial class FileHistoryViewModel(GitRepository repository) : ViewModelBase
{
    public ObservableCollection<FileHistoryEntryViewModel> Entries { get; } = [];

    public DiffViewModel Diff { get; } = new();

    [ObservableProperty]
    public partial string Path { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool FollowRenames { get; set; } = true;

    [ObservableProperty]
    public partial FileHistoryEntryViewModel? SelectedEntry { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    public string Title => $"History — {Path}";

    /// <summary>The in-flight diff load, exposed so tests can await selection side effects.</summary>
    internal Task PendingDiffLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The in-flight reload, so callers can await a toggle deterministically.</summary>
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        Path = path;
        OnPropertyChanged(nameof(Title));

        Entries.Clear();
        var history = await repository.Files
            .GetFileHistoryAsync(path, followRenames: FollowRenames, ct: ct)
            .ConfigureAwait(true);

        foreach (var commit in history)
            Entries.Add(new FileHistoryEntryViewModel(commit));

        StatusText = history.Count == 1 ? "1 commit touched this file" : $"{history.Count:N0} commits touched this file";

        SelectedEntry = Entries.FirstOrDefault();
        await PendingDiffLoad.ConfigureAwait(true);
    }

    partial void OnSelectedEntryChanged(FileHistoryEntryViewModel? value)
    {
        PendingDiffLoad = LoadDiffAsync(value);
    }

    partial void OnFollowRenamesChanged(bool value) => PendingLoad = LoadAsync(Path);

    private async Task LoadDiffAsync(FileHistoryEntryViewModel? entry)
    {
        if (entry is null)
        {
            Diff.Load(null, Path);
            return;
        }

        var change = new FileChange(ChangeKind.Modified, Path);
        var diff = await repository
            .GetCommitFileDiffStructuredAsync(entry.Sha, change, Diff.Options)
            .ConfigureAwait(true);

        Diff.Load(diff, Path);
    }
}
