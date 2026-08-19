using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using GitFork.App.Controls;
using GitFork.Core;
using GitFork.Core.Graph;

namespace GitFork.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private AppSettings _settings = AppSettings.Default;
    private IReadOnlyDictionary<string, SignatureStatus> _signatures =
        new Dictionary<string, SignatureStatus>(StringComparer.Ordinal);

    private GitRepository? _repository;
    private CancellationTokenSource? _detailCts;
    private RepositoryWatcher? _watcher;

    /// <summary>Set to false in tests, where the watcher would fire during assertions.</summary>
    public bool WatchForChanges { get; init; } = true;

    /// <summary>Set by the view; opens a native folder picker and returns the chosen path.</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>Set by the view; shows a modal yes/no. Required before anything destructive.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Set by the view; asks the user for one line of text. Null when cancelled.</summary>
    public Func<PromptRequest, Task<string?>>? PromptAsync { get; set; }

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

    [ObservableProperty]
    public partial WorkingCopyViewModel? WorkingCopy { get; set; }

    /// <summary>Repository-level actions: fetch/pull/push, branches, tags, history, stashes.</summary>
    [ObservableProperty]
    public partial RepositoryCommandsViewModel? Commands { get; set; }

    /// <summary>True while the pinned "Uncommitted changes" row owns the detail pane.</summary>
    [ObservableProperty]
    public partial bool IsWorkingCopySelected { get; set; }

    [ObservableProperty]
    public partial WorkingTreeStatus Status { get; set; } = WorkingTreeStatus.Empty;

    /// <summary>Whatever the lower pane is currently showing: a commit, or the working copy.</summary>
    public object? DetailContent => IsWorkingCopySelected ? WorkingCopy : Detail;

    public bool HasUncommittedChanges => !Status.IsClean;

    public string UncommittedSummary => Status.TotalChanges == 1
        ? "1 changed file"
        : $"{Status.TotalChanges} changed files";



    public bool HasRepository => _repository is not null;

    /// <summary>The open repository, for views that need to start their own queries.</summary>
    public GitRepository? Repository => _repository;

    /// <summary>What the history is narrowed to; empty means everything.</summary>
    [ObservableProperty]
    public partial CommitFilter Filter { get; set; } = CommitFilter.Empty;

    // The filter boxes bind to plain strings; they are gathered into a CommitFilter on search.
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilterAuthor { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilterPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasMoreCommits { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    public bool IsFiltered => !Filter.IsEmpty;

    public string FilterDescription => Filter.Describe();

    /// <summary>Applies settings that affect this repository's views.</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;

        if (Detail is { } detail)
        {
            detail.Diff.ContextLines = settings.DiffContextLines;
            detail.Diff.Whitespace = settings.DiffWhitespace;
            detail.Diff.ShowSyntaxHighlighting = settings.ShowSyntaxHighlighting;
            detail.Diff.ShowWordHighlighting = settings.ShowWordHighlighting;
        }
    }

    /// <summary>Re-reads history with the current filter.</summary>
    [RelayCommand]
    private async Task ApplyFilterAsync()
    {
        Filter = new CommitFilter
        {
            Text = FilterText,
            Author = FilterAuthor,
            Path = FilterPath,
        };

        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(FilterDescription));

        if (_repository is not null)
            await LoadAsync(_repository).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearFilterAsync()
    {
        FilterText = string.Empty;
        FilterAuthor = string.Empty;
        FilterPath = string.Empty;
        await ApplyFilterAsync().ConfigureAwait(true);
    }

    /// <summary>Appends the next page of history rather than reloading what is already shown.</summary>
    [RelayCommand]
    private async Task LoadMoreCommitsAsync()
    {
        if (_repository is null || !HasMoreCommits || IsLoadingMore)
            return;

        IsLoadingMore = true;
        try
        {
            var page = await _repository
                .GetCommitPageAsync(_settings.CommitPageSize, skip: Commits.Count, filter: Filter)
                .ConfigureAwait(true);

            // The graph is laid out over the whole list, so it has to be rebuilt from all of it.
            var all = Commits.Select(c => c.Commit).Concat(page.Commits).ToList();
            var refs = await _repository.GetRefsAsync().ConfigureAwait(true);

            PopulateCommits(all, refs);
            HasMoreCommits = page.HasMore;
        }
        catch (GitException ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    /// <summary>The in-flight detail load, exposed so tests can await selection side effects.</summary>
    internal Task PendingDetailLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The in-flight diff load of the selected commit, for tests to await.</summary>
    internal Task PendingDiffLoad => Detail?.PendingDiffLoad ?? Task.CompletedTask;

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
            WorkingCopy = new WorkingCopyViewModel(repository) { ConfirmDiscardAsync = ConfirmAsync };
            WorkingCopy.RepositoryChanged += OnRepositoryChanged;

            Commands?.Dispose();
            Commands = new RepositoryCommandsViewModel(repository)
            {
                ConfirmAsync = ConfirmAsync,
                PromptAsync = PromptAsync,
            };
            Commands.RepositoryChanged += OnRepositoryChanged;

            StartWatching(repository.RootPath);
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
            var commitsTask = repository.GetCommitPageAsync(_settings.CommitPageSize, skip: 0, filter: Filter);
            var refsTask = repository.GetRefsAsync();
            var branchTask = repository.GetCurrentBranchAsync();
            var statusTask = repository.GetStatusAsync();
            var stashesTask = repository.Stashes.GetStashesAsync();
            var signaturesTask = repository.Integrations.GetSignatureStatusAsync();

            var page = await commitsTask.ConfigureAwait(true);
            var commits = page.Commits;
            HasMoreCommits = page.HasMore;
            var refs = await refsTask.ConfigureAwait(true);
            CurrentBranch = await branchTask.ConfigureAwait(true) ?? "detached HEAD";
            var status = await statusTask.ConfigureAwait(true);
            var stashes = await stashesTask.ConfigureAwait(true);
            _signatures = await signaturesTask.ConfigureAwait(true);

            PopulateCommits(commits, refs);
            PopulateSidebar(refs, stashes);

            Status = status;
            OnPropertyChanged(nameof(HasUncommittedChanges));
            OnPropertyChanged(nameof(UncommittedSummary));

            if (Commands is { } commands)
                await commands.RefreshStateAsync().ConfigureAwait(true);

            if (WorkingCopy is { } workingCopy)
                await workingCopy.LoadAsync(status).ConfigureAwait(true);

            // A commit or a discard can leave nothing to show; fall back to the newest commit.
            if (IsWorkingCopySelected && status.IsClean)
                SelectCommitRow(Commits.FirstOrDefault());

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

    private void PopulateCommits(IReadOnlyList<Commit> commits, IReadOnlyList<GitRef> refs)
    {
        var rows = CommitGraphBuilder.Build(commits);

        // Decoration names alone cannot distinguish "feature/login" from "origin/main",
        // so badges are classified against the real ref list.
        var refKinds = refs
            .GroupBy(r => r.ShortName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);

        // One shared column width keeps every row's lanes vertically aligned.
        var maxLanes = rows.Count == 0 ? 1 : rows.Max(r => r.LaneCount);
        var graphWidth = Math.Max(maxLanes, 1) * GraphRowControl.LaneWidth;

        var previouslySelected = SelectedCommit?.Sha;
        var wasOnWorkingCopy = IsWorkingCopySelected;

        Commits.Clear();
        for (var i = 0; i < commits.Count; i++)
            Commits.Add(new CommitRowViewModel(
                commits[i], rows[i], graphWidth, refKinds,
                _signatures.GetValueOrDefault(commits[i].Sha, SignatureStatus.None)));

        // Keep the user on the same commit across a refresh rather than jumping to the top.
        SelectedCommit = Commits.FirstOrDefault(c => c.Sha == previouslySelected) ?? Commits.FirstOrDefault();

        // Assigning SelectedCommit hands the lower pane back to the commit view. With the file
        // watcher running, that would throw the user out of the staging pane on every save.
        if (wasOnWorkingCopy)
            IsWorkingCopySelected = true;
    }

    private void PopulateSidebar(IReadOnlyList<GitRef> refs, IReadOnlyList<StashEntry> stashes)
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

        // "git for-each-ref" only ever reports refs/stash, which is the top of the stack, so the
        // stash list is read from the reflog instead: otherwise only one stash is ever visible
        // and every apply/pop/drop would target stash@{0}.
        var stashSection = new SidebarSectionViewModel("Stashes");
        foreach (var stash in stashes)
        {
            var stashRef = new GitRef(
                FullName: stash.Reference,
                ShortName: stash.DisplayMessage,
                Kind: RefKind.Stash,
                TargetSha: string.Empty,
                Upstream: null,
                Ahead: 0,
                Behind: 0,
                IsHead: false);

            stashSection.Items.Add(new SidebarItemViewModel(stashRef, stash.DisplayMessage));
        }

        AddIfPopulated(stashSection);
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
        if (value is not null)
            IsWorkingCopySelected = false;
        PendingDetailLoad = LoadDetailAsync(value);
    }

    partial void OnIsWorkingCopySelectedChanged(bool value) => OnPropertyChanged(nameof(DetailContent));

    partial void OnDetailChanged(CommitDetailViewModel? value) => OnPropertyChanged(nameof(DetailContent));

    partial void OnWorkingCopyChanged(WorkingCopyViewModel? value) => OnPropertyChanged(nameof(DetailContent));

    /// <summary>Switches the lower pane to the working copy, as the pinned row does.</summary>
    [RelayCommand]
    public void SelectWorkingCopy()
    {
        SelectedCommit = null;
        IsWorkingCopySelected = true;
    }

    private void SelectCommitRow(CommitRowViewModel? row)
    {
        IsWorkingCopySelected = false;
        SelectedCommit = row;
    }

    private void OnRepositoryChanged(object? sender, EventArgs e) => _ = RefreshAsync();

    /// <summary>Reloads when the repository changes underneath us — an editor save, a terminal commit.</summary>
    private void StartWatching(string rootPath)
    {
        _watcher?.Dispose();
        _watcher = null;

        if (!WatchForChanges)
            return;

        var watcher = new RepositoryWatcher(rootPath);
        watcher.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        watcher.Start();
        _watcher = watcher;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _detailCts?.Dispose();
        _detailCts = null;
        Commands?.Dispose();
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

            var vm = new CommitDetailViewModel(_repository)
            {
                Commit = detail.Commit,
                Body = detail.Body,
                Files = [.. detail.Files.Select(f => new FileChangeViewModel(f))],
            };

            vm.WireOptions();
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

    private void DetachDetail() => Detail = null;


    /// <summary>Selects the commit a sidebar ref points at, so clicking a branch jumps to its tip.</summary>
    public void SelectCommitBySha(string sha)
    {
        var match = Commits.FirstOrDefault(c => c.Sha == sha);
        if (match is not null)
            SelectedCommit = match;
    }
}
