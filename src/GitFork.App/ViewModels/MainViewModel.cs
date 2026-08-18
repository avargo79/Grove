using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitFork.App.Controls;
using GitFork.Core;
using GitFork.Core.Graph;

namespace GitFork.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private const int CommitPageSize = 2000;

    private GitRepository? _repository;
    private CancellationTokenSource? _detailCts;

    /// <summary>Set by the view; opens a native folder picker and returns the chosen path.</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    public ObservableCollection<CommitRowViewModel> Commits { get; } = [];
    public ObservableCollection<SidebarSectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    public partial string RepositoryName { get; set; } = "No repository";

    [ObservableProperty]
    public partial string? RepositoryPath { get; set; }

    [ObservableProperty]
    public partial string? CurrentBranch { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; } = "Open a repository to get started.";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial CommitRowViewModel? SelectedCommit { get; set; }

    [ObservableProperty]
    public partial CommitDetailViewModel? Detail { get; set; }

    public ObservableCollection<DiffLineViewModel> DiffLines { get; } = [];

    [ObservableProperty]
    public partial bool IsDiffLoading { get; set; }

    public bool HasRepository => _repository is not null;

    // ------------------------------------------------------------- commands

    [RelayCommand]
    private async Task OpenRepositoryAsync()
    {
        if (PickFolderAsync is null)
            return;

        var path = await PickFolderAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(path))
            await LoadRepositoryAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_repository is not null)
            await LoadAsync(_repository).ConfigureAwait(true);
    }

    public async Task LoadRepositoryAsync(string path)
    {
        IsBusy = true;
        StatusMessage = "Opening repository...";
        try
        {
            var repository = await GitRepository.OpenAsync(path).ConfigureAwait(true);
            if (repository is null)
            {
                StatusMessage = $"'{path}' is not inside a git repository.";
                return;
            }

            _repository = repository;
            RepositoryPath = repository.RootPath;
            RepositoryName = repository.Name;
            OnPropertyChanged(nameof(HasRepository));

            await LoadAsync(repository).ConfigureAwait(true);
        }
        catch (GitException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --------------------------------------------------------------- loading

    private async Task LoadAsync(GitRepository repository)
    {
        IsBusy = true;
        try
        {
            var commitsTask = repository.GetCommitsAsync(CommitPageSize);
            var refsTask = repository.GetRefsAsync();
            var branchTask = repository.GetCurrentBranchAsync();
            var statusTask = repository.GetStatusAsync();

            var commits = await commitsTask.ConfigureAwait(true);
            var refs = await refsTask.ConfigureAwait(true);
            CurrentBranch = await branchTask.ConfigureAwait(true) ?? "detached HEAD";
            var status = await statusTask.ConfigureAwait(true);

            PopulateCommits(commits);
            PopulateSidebar(refs);

            StatusMessage = BuildStatusLine(commits.Count, status);
        }
        catch (GitException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PopulateCommits(IReadOnlyList<Commit> commits)
    {
        var rows = CommitGraphBuilder.Build(commits);

        // One shared column width keeps every row's lanes vertically aligned.
        var maxLanes = rows.Count == 0 ? 1 : rows.Max(r => r.LaneCount);
        var graphWidth = Math.Max(maxLanes, 1) * GraphRowControl.LaneWidth;

        Commits.Clear();
        for (var i = 0; i < commits.Count; i++)
            Commits.Add(new CommitRowViewModel(commits[i], rows[i], graphWidth));

        SelectedCommit = Commits.FirstOrDefault();
    }

    private void PopulateSidebar(IReadOnlyList<GitRef> refs)
    {
        Sections.Clear();

        var locals = new SidebarSectionViewModel("Branches");
        foreach (var r in refs.Where(r => r.Kind == RefKind.LocalBranch).OrderBy(r => r.ShortName, StringComparer.OrdinalIgnoreCase))
            locals.Items.Add(new SidebarItemViewModel(r, r.ShortName));
        AddIfPopulated(locals);

        var remotes = new SidebarSectionViewModel("Remotes");
        foreach (var group in refs.Where(r => r.Kind == RefKind.RemoteBranch)
                     .GroupBy(r => r.RemoteName ?? "origin")
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            // Header row for the remote itself, then its branches indented beneath.
            var header = group.First();
            remotes.Items.Add(new SidebarItemViewModel(
                header with { ShortName = group.Key, Kind = RefKind.Other }, group.Key));

            foreach (var r in group.OrderBy(r => r.NameWithinRemote, StringComparer.OrdinalIgnoreCase))
                remotes.Items.Add(new SidebarItemViewModel(r, r.NameWithinRemote, indentLevel: 1));
        }
        AddIfPopulated(remotes);

        var tags = new SidebarSectionViewModel("Tags") { IsExpanded = false };
        foreach (var r in refs.Where(r => r.Kind == RefKind.Tag).OrderByDescending(r => r.ShortName, StringComparer.OrdinalIgnoreCase))
            tags.Items.Add(new SidebarItemViewModel(r, r.ShortName));
        AddIfPopulated(tags);

        var stashes = new SidebarSectionViewModel("Stashes");
        foreach (var r in refs.Where(r => r.Kind == RefKind.Stash))
            stashes.Items.Add(new SidebarItemViewModel(r, r.ShortName));
        AddIfPopulated(stashes);
    }

    private void AddIfPopulated(SidebarSectionViewModel section)
    {
        if (section.Items.Count > 0)
            Sections.Add(section);
    }

    private static string BuildStatusLine(int commitCount, WorkingTreeStatus status)
    {
        var commitText = commitCount == 1 ? "1 commit" : $"{commitCount:N0} commits";
        if (status.IsClean)
            return $"{commitText} · working tree clean";

        var parts = new List<string>();
        if (status.Staged.Count > 0)
            parts.Add($"{status.Staged.Count} staged");
        if (status.Unstaged.Count > 0)
            parts.Add($"{status.Unstaged.Count} changed");
        if (status.Untracked.Count > 0)
            parts.Add($"{status.Untracked.Count} untracked");

        return $"{commitText} · {string.Join(", ", parts)}";
    }

    // ---------------------------------------------------- selection handling

    partial void OnSelectedCommitChanged(CommitRowViewModel? value)
    {
        _ = LoadDetailAsync(value);
    }

    private async Task LoadDetailAsync(CommitRowViewModel? row)
    {
        // Cancel any in-flight load so fast arrow-key scrolling does not queue up work.
        if (_detailCts is { } previousCts)
        {
            await previousCts.CancelAsync().ConfigureAwait(true);
            previousCts.Dispose();
        }

        _detailCts = null;

        DiffLines.Clear();
        DetachDetail();

        if (_repository is null || row is null)
        {
            Detail = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _detailCts = cts;

        try
        {
            var detail = await _repository.GetCommitDetailAsync(row.Commit, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;

            var vm = new CommitDetailViewModel
            {
                Commit = detail.Commit,
                Body = detail.Body,
                Files = [.. detail.Files.Select(f => new FileChangeViewModel(f))],
            };

            vm.PropertyChanged += OnDetailPropertyChanged;
            Detail = vm;
            vm.SelectedFile = vm.Files.Count > 0 ? vm.Files[0] : null;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        catch (GitException ex)
        {
            StatusMessage = ex.Message;
            Detail = null;
        }
    }

    private void DetachDetail()
    {
        if (Detail is { } previous)
            previous.PropertyChanged -= OnDetailPropertyChanged;
    }

    private void OnDetailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommitDetailViewModel.SelectedFile) && sender is CommitDetailViewModel detail)
            _ = LoadDiffAsync(detail.SelectedFile);
    }

    private async Task LoadDiffAsync(FileChangeViewModel? file)
    {
        DiffLines.Clear();
        if (_repository is null || file is null || Detail is null)
            return;

        var token = _detailCts?.Token ?? CancellationToken.None;
        IsDiffLoading = true;
        try
        {
            var lines = await _repository
                .GetCommitFileDiffAsync(Detail.Sha, file.Change, ct: token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested)
                return;

            foreach (var line in lines)
                DiffLines.Add(new DiffLineViewModel(line));
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (GitException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsDiffLoading = false;
        }
    }

    /// <summary>Selects the commit a sidebar ref points at, so clicking a branch jumps to its tip.</summary>
    public void SelectCommitBySha(string sha)
    {
        var match = Commits.FirstOrDefault(c => c.Sha == sha);
        if (match is not null)
            SelectedCommit = match;
    }
}
