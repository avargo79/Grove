using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Grove.Core;

namespace Grove.App.ViewModels;

/// <summary>A changed file in the selected commit.</summary>
// Not sealed: WorkingFileViewModel extends it with which side of the index it sits on.
public class FileChangeViewModel(FileChange change)
{
    public FileChange Change { get; } = change;
    public string FileName => Change.FileName;
    public string Directory => Change.Directory;
    public bool HasDirectory => Directory.Length > 0;
    public string ToolTip => Change.DisplayPath;

    /// <summary>Single-letter status marker, as git itself reports it.</summary>
    public string StatusGlyph => Change.Kind switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Modified => "M",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Copied => "C",
        ChangeKind.TypeChanged => "T",
        ChangeKind.Unmerged => "U",
        _ => "?",
    };

    // Style classes bind to these so the status letter takes its colour from the theme.
    public bool IsAdded => Change.Kind == ChangeKind.Added;
    public bool IsDeleted => Change.Kind == ChangeKind.Deleted;
    public bool IsRenamed => Change.Kind is ChangeKind.Renamed or ChangeKind.Copied;
    public bool IsConflict => Change.Kind == ChangeKind.Unmerged;
}

/// <summary>One line of a rendered diff, with gutter numbers already formatted.</summary>
public sealed class DiffLineViewModel(DiffLine line)
{
    private const int GutterWidth = 5;

    public DiffLine Line { get; } = line;
    public string Text => Line.Text;
    public DiffLineKind Kind => Line.Kind;

    public string OldNumber => Line.OldLineNumber?.ToString(CultureInfo.InvariantCulture).PadLeft(GutterWidth) ?? new string(' ', GutterWidth);
    public string NewNumber => Line.NewLineNumber?.ToString(CultureInfo.InvariantCulture).PadLeft(GutterWidth) ?? new string(' ', GutterWidth);

    public string Marker => Line.Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };

    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsHunkHeader => Kind == DiffLineKind.HunkHeader;
    public bool IsHeader => Kind is DiffLineKind.Header or DiffLineKind.NoNewline;
}

/// <summary>The detail pane: commit metadata, its file list, and the diff for the selected file.</summary>
public sealed partial class CommitDetailViewModel(GitRepository repository) : ViewModelBase, IDisposable
{
    private CancellationTokenSource? _diffCts;

    public required Commit Commit { get; init; }
    public required string Body { get; init; }
    public required IReadOnlyList<FileChangeViewModel> Files { get; init; }

    /// <summary>Drives the diff pane; owned here so the file list binds straight to it.</summary>
    [ObservableProperty]
    public partial FileChangeViewModel? SelectedFile { get; set; }

    /// <summary>The diff pane: unified or side-by-side, with its own presentation options.</summary>
    public DiffViewModel Diff { get; } = new();

    /// <summary>The in-flight diff load, exposed so tests can await selection side effects.</summary>
    internal Task PendingDiffLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Re-reads the diff when an option changes what git is asked for.</summary>
    public void WireOptions() => Diff.OptionsChanged += (_, _) => PendingDiffLoad = LoadDiffAsync(SelectedFile);

    partial void OnSelectedFileChanged(FileChangeViewModel? value)
    {
        PendingDiffLoad = LoadDiffAsync(value);
    }

    public void Dispose()
    {
        _diffCts?.Dispose();
        _diffCts = null;
        Diff.Dispose();
    }

    private async Task LoadDiffAsync(FileChangeViewModel? file)
    {
        // Arrowing through the file list must not queue up a load per keystroke.
        if (_diffCts is { } previous)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        if (file is null)
        {
            _diffCts = null;
            Diff.Load(null, string.Empty);
            return;
        }

        var cts = new CancellationTokenSource();
        _diffCts = cts;

        try
        {
            // An image has no useful text diff; show the two versions instead.
            if (GitFileOperations.IsImagePath(file.Change.Path))
            {
                await LoadImageAsync(file, cts.Token).ConfigureAwait(true);
                return;
            }

            var diff = await repository
                .GetCommitFileDiffStructuredAsync(Commit.Sha, file.Change, Diff.Options, cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested)
                return;

            Diff.Load(diff, file.Change.Path);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
    }

    private async Task LoadImageAsync(FileChangeViewModel file, CancellationToken ct)
    {
        // The parent revision is where the "before" comes from; a root commit has none.
        var parent = Commit.ParentShas.Count > 0 ? Commit.ParentShas[0] : null;

        var before = parent is null
            ? null
            : await repository.Files.GetBlobAsync(parent, file.Change.OldPath ?? file.Change.Path, ct)
                .ConfigureAwait(true);

        var after = file.Change.Kind == ChangeKind.Deleted
            ? null
            : await repository.Files.GetBlobAsync(Commit.Sha, file.Change.Path, ct).ConfigureAwait(true);

        if (!ct.IsCancellationRequested)
            Diff.LoadImage(ImageDiffViewModel.Create(before, after));
    }

    public string Subject => Commit.Subject;
    public string Sha => Commit.Sha;
    public string ShortSha => Commit.ShortSha;
    public string AuthorLine => $"{Commit.AuthorName} <{Commit.AuthorEmail}>";
    public string AuthoredOn => Commit.AuthorDate.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public bool HasBody => Body.Length > 0;
    public string ParentsDisplay => string.Join(", ", Commit.ParentShas.Select(p => p[..Math.Min(7, p.Length)]));
    public bool HasParents => Commit.ParentShas.Count > 0;
    public string FileCountDisplay => Files.Count == 1 ? "1 file changed" : $"{Files.Count} files changed";
}
