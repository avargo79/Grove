using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grove.Core;

namespace Grove.App.ViewModels;

/// <summary>A file in the working copy, on one side of the index.</summary>
public sealed class WorkingFileViewModel(FileChange change, bool isStaged, bool isUntracked)
    : FileChangeViewModel(change)
{
    public bool IsStaged { get; } = isStaged;
    public bool IsUntracked { get; } = isUntracked;
}

/// <summary>Whether a diff row is a hunk heading or one line of the hunk body.</summary>
public enum DiffRowKind { HunkHeader, Line }

/// <summary>
/// One row of the staging diff. Rows carry their hunk and line indices so a selection in the UI
/// maps straight onto <see cref="PatchBuilder"/> without re-parsing anything.
/// </summary>
public sealed partial class DiffRowViewModel : ViewModelBase
{
    private const int GutterWidth = 5;

    public required DiffRowKind Kind { get; init; }
    public required int HunkIndex { get; init; }

    /// <summary>Index within the hunk's line list; -1 for a hunk header row.</summary>
    public required int LineIndex { get; init; }

    public required string Text { get; init; }
    public DiffLineKind LineKind { get; init; } = DiffLineKind.Context;
    public int? OldLineNumber { get; init; }
    public int? NewLineNumber { get; init; }

    public bool IsHunkHeader => Kind == DiffRowKind.HunkHeader;
    public bool IsAdded => LineKind == DiffLineKind.Added;
    public bool IsRemoved => LineKind == DiffLineKind.Removed;
    public bool IsHeader => LineKind is DiffLineKind.Header or DiffLineKind.NoNewline;

    /// <summary>Only additions and removals can be staged individually.</summary>
    public bool IsStageable => Kind == DiffRowKind.Line && LineKind is DiffLineKind.Added or DiffLineKind.Removed;

    public string OldNumber => Format(OldLineNumber);
    public string NewNumber => Format(NewLineNumber);

    public string Marker => LineKind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };

    private static string Format(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(GutterWidth)
        ?? new string(' ', GutterWidth);

    public static DiffRowViewModel ForHunk(int hunkIndex, DiffHunk hunk) => new()
    {
        Kind = DiffRowKind.HunkHeader,
        HunkIndex = hunkIndex,
        LineIndex = -1,
        Text = hunk.Header,
        LineKind = DiffLineKind.HunkHeader,
    };

    public static DiffRowViewModel ForLine(int hunkIndex, int lineIndex, DiffLine line) => new()
    {
        Kind = DiffRowKind.Line,
        HunkIndex = hunkIndex,
        LineIndex = lineIndex,
        Text = line.Text,
        LineKind = line.Kind,
        OldLineNumber = line.OldLineNumber,
        NewLineNumber = line.NewLineNumber,
    };
}

/// <summary>
/// The working copy pane: staged and unstaged file lists, a diff that supports hunk- and
/// line-level staging, and the commit box.
/// </summary>
public sealed partial class WorkingCopyViewModel(GitRepository repository) : ViewModelBase
{
    private readonly GitWorkingCopy _workingCopy = repository.WorkingCopy;
    private FileDiff? _currentDiff;
    private CancellationTokenSource? _diffCts;

    /// <summary>The in-flight diff load, exposed so tests and callers can await it.</summary>
    internal Task PendingDiffLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Raised after any operation that changes the repository, so the shell can reload.</summary>
    public event EventHandler? RepositoryChanged;

    /// <summary>
    /// Set by the view to confirm a destructive discard. Returns true to proceed. Discards are
    /// unrecoverable, so a null hook is treated as "declined" rather than "go ahead".
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDiscardAsync { get; set; }

    public ObservableCollection<WorkingFileViewModel> StagedFiles { get; } = [];
    public ObservableCollection<WorkingFileViewModel> UnstagedFiles { get; } = [];
    public ObservableCollection<DiffRowViewModel> DiffRows { get; } = [];
    public ObservableCollection<string> RecentMessages { get; } = [];

    /// <summary>Rows the user has highlighted, for line-level staging. Kept in sync by the view.</summary>
    public ObservableCollection<DiffRowViewModel> SelectedRows { get; } = [];

    [ObservableProperty]
    public partial WorkingFileViewModel? SelectedFile { get; set; }

    [ObservableProperty]
    public partial string CommitMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAmending { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasStagedFiles => StagedFiles.Count > 0;

    /// <summary>Amending can rewrite HEAD's message with nothing newly staged, so it bypasses the check.</summary>
    public bool CanCommit => !string.IsNullOrWhiteSpace(CommitMessage) && (HasStagedFiles || IsAmending);

    public string CommitButtonText => IsAmending ? "Amend Commit" : "Commit";

    public string SummaryText
    {
        get
        {
            var total = StagedFiles.Count + UnstagedFiles.Count;
            return total == 1 ? "1 changed file" : $"{total} changed files";
        }
    }

    // ------------------------------------------------------------- loading

    public async Task LoadAsync(WorkingTreeStatus status, CancellationToken ct = default)
    {
        var previouslySelected = SelectedFile?.Change.Path;
        var wasStaged = SelectedFile?.IsStaged ?? false;

        StagedFiles.Clear();
        UnstagedFiles.Clear();

        foreach (var file in status.Staged)
            StagedFiles.Add(new WorkingFileViewModel(file, isStaged: true, isUntracked: false));
        foreach (var file in status.Unstaged)
            UnstagedFiles.Add(new WorkingFileViewModel(file, isStaged: false, isUntracked: false));
        foreach (var file in status.Untracked)
            UnstagedFiles.Add(new WorkingFileViewModel(file, isStaged: false, isUntracked: true));

        OnPropertyChanged(nameof(HasStagedFiles));
        OnPropertyChanged(nameof(SummaryText));
        CommitCommand.NotifyCanExecuteChanged();

        // Keep the user on the same file across a refresh where possible.
        // Assigning SelectedFile starts the reload; awaiting it here rather than starting a
        // second one is what keeps the two from interleaving and doubling up the rows.
        SelectedFile = FindFile(previouslySelected, wasStaged)
                       ?? FindFile(previouslySelected, !wasStaged)
                       ?? UnstagedFiles.FirstOrDefault()
                       ?? StagedFiles.FirstOrDefault();

        await PendingDiffLoad.ConfigureAwait(true);

        RecentMessages.Clear();
        foreach (var message in await _workingCopy.GetRecentMessagesAsync(ct: ct).ConfigureAwait(true))
            RecentMessages.Add(message);
    }

    private WorkingFileViewModel? FindFile(string? path, bool staged)
    {
        if (path is null)
            return null;
        var source = staged ? StagedFiles : UnstagedFiles;
        return source.FirstOrDefault(f => f.Change.Path == path);
    }

    partial void OnSelectedFileChanged(WorkingFileViewModel? value) => PendingDiffLoad = ReloadDiffAsync();

    partial void OnCommitMessageChanged(string value) => CommitCommand.NotifyCanExecuteChanged();

    partial void OnIsAmendingChanged(bool value)
    {
        OnPropertyChanged(nameof(CommitButtonText));
        CommitCommand.NotifyCanExecuteChanged();
        _ = PrefillAmendMessageAsync(value);
    }

    private async Task PrefillAmendMessageAsync(bool amending)
    {
        // Turning amend on offers HEAD's message; turning it off clears the borrowed text.
        if (!amending)
        {
            CommitMessage = string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(CommitMessage))
            CommitMessage = await _workingCopy.GetHeadMessageAsync().ConfigureAwait(true);
    }

    private async Task ReloadDiffAsync()
    {
        // Supersede any load still running, so rows are only ever appended by the newest one.
        if (_diffCts is { } previous)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _diffCts = cts;

        DiffRows.Clear();
        SelectedRows.Clear();
        _currentDiff = null;

        if (SelectedFile is not { } file)
            return;

        try
        {
            _currentDiff = await _workingCopy.GetFileDiffAsync(
                file.Change,
                file.IsStaged ? DiffSide.Staged : DiffSide.Unstaged,
                file.IsUntracked,
                ct: cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        if (_currentDiff is null || cts.IsCancellationRequested)
            return;

        if (_currentDiff.IsBinary)
        {
            DiffRows.Add(new DiffRowViewModel
            {
                Kind = DiffRowKind.Line,
                HunkIndex = -1,
                LineIndex = -1,
                Text = "Binary file — no text diff available.",
                LineKind = DiffLineKind.Header,
            });
            return;
        }

        for (var h = 0; h < _currentDiff.Hunks.Count; h++)
        {
            var hunk = _currentDiff.Hunks[h];
            DiffRows.Add(DiffRowViewModel.ForHunk(h, hunk));
            for (var l = 0; l < hunk.Lines.Count; l++)
                DiffRows.Add(DiffRowViewModel.ForLine(h, l, hunk.Lines[l]));
        }
    }

    // ------------------------------------------------------ file commands

    [RelayCommand]
    private Task StageFileAsync(WorkingFileViewModel? file) =>
        RunAsync(() => _workingCopy.StageAsync([PathOf(file)]), file);

    [RelayCommand]
    private Task UnstageFileAsync(WorkingFileViewModel? file) =>
        RunAsync(() => _workingCopy.UnstageAsync([PathOf(file)]), file);

    [RelayCommand]
    private Task StageAllAsync() =>
        RunAsync(() => _workingCopy.StageAsync([.. UnstagedFiles.Select(f => f.Change.Path)]), null);

    [RelayCommand]
    private Task UnstageAllAsync() =>
        RunAsync(() => _workingCopy.UnstageAsync([.. StagedFiles.Select(f => f.Change.Path)]), null);

    /// <summary>Destructive: always routed through <see cref="ConfirmDiscardAsync"/> first.</summary>
    [RelayCommand]
    private async Task DiscardFileAsync(WorkingFileViewModel? file)
    {
        if (file is null)
            return;

        var prompt = file.IsUntracked
            ? $"Delete the untracked file '{file.Change.Path}'? This cannot be undone."
            : $"Discard all changes to '{file.Change.Path}'? This cannot be undone.";

        var confirm = ConfirmDiscardAsync;
        if (confirm is null || !await confirm(prompt).ConfigureAwait(true))
            return;

        await RunAsync(
            () => file.IsUntracked
                ? _workingCopy.DeleteUntrackedAsync([file.Change.Path])
                : _workingCopy.DiscardChangesAsync([file.Change.Path]),
            null).ConfigureAwait(true);
    }

    // ------------------------------------------------- hunk/line commands

    [RelayCommand]
    private Task StageHunkAsync(DiffRowViewModel? row) => ApplyHunkAsync(row, PatchDirection.Stage);

    [RelayCommand]
    private Task UnstageHunkAsync(DiffRowViewModel? row) => ApplyHunkAsync(row, PatchDirection.Unstage);

    private Task ApplyHunkAsync(DiffRowViewModel? row, PatchDirection direction)
    {
        if (_currentDiff is null || row is null || row.HunkIndex < 0)
            return Task.CompletedTask;

        var patch = PatchBuilder.BuildHunkPatch(_currentDiff, [row.HunkIndex], direction);
        return patch is null ? Task.CompletedTask : RunAsync(() => _workingCopy.ApplyToIndexAsync(patch, direction), null);
    }

    [RelayCommand]
    private Task StageSelectedLinesAsync() => ApplySelectedLinesAsync(PatchDirection.Stage);

    [RelayCommand]
    private Task UnstageSelectedLinesAsync() => ApplySelectedLinesAsync(PatchDirection.Unstage);

    private Task ApplySelectedLinesAsync(PatchDirection direction)
    {
        if (_currentDiff is null)
            return Task.CompletedTask;

        var selection = new Dictionary<int, IReadOnlySet<int>>();
        foreach (var group in SelectedRows.Where(r => r.IsStageable).GroupBy(r => r.HunkIndex))
            selection[group.Key] = group.Select(r => r.LineIndex).ToHashSet();

        if (selection.Count == 0)
            return Task.CompletedTask;

        var patch = PatchBuilder.BuildSelectionPatch(_currentDiff, selection, direction);
        return patch is null ? Task.CompletedTask : RunAsync(() => _workingCopy.ApplyToIndexAsync(patch, direction), null);
    }

    /// <summary>True when the highlighted rows contain something that can be staged.</summary>
    public bool HasStageableSelection => SelectedRows.Any(r => r.IsStageable);

    public void NotifySelectionChanged() => OnPropertyChanged(nameof(HasStageableSelection));

    // ----------------------------------------------------- commit command

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        try
        {
            await _workingCopy.CommitAsync(CommitMessage, IsAmending).ConfigureAwait(true);
            CommitMessage = string.Empty;
            IsAmending = false;
            ErrorMessage = null;
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void UseRecentMessage(string? message)
    {
        if (!string.IsNullOrEmpty(message))
            CommitMessage = message;
    }

    // ------------------------------------------------------------ helpers

    private static string PathOf(WorkingFileViewModel? file) => file?.Change.Path ?? string.Empty;

    /// <summary>Runs a git operation, surfaces any failure, and asks the shell to reload.</summary>
    private async Task RunAsync(Func<Task> operation, WorkingFileViewModel? file)
    {
        if (file is not null && file.Change.Path.Length == 0)
            return;

        try
        {
            ErrorMessage = null;
            await operation().ConfigureAwait(true);
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
