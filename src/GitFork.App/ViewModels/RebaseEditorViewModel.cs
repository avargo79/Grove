using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitFork.Core;

namespace GitFork.App.ViewModels;

/// <summary>One commit in the rebase plan, with the action chosen for it.</summary>
public sealed partial class RebaseRowViewModel(RebaseTodoItem item) : ViewModelBase
{
    [ObservableProperty]
    public partial RebaseAction Action { get; set; } = item.Action;

    public string Sha { get; } = item.Sha;
    public string ShortSha { get; } = item.ShortSha;
    public string Subject { get; } = item.Subject;

    public RebaseTodoItem ToItem() => new(Action, Sha, Subject);

    public bool IsDropped => Action == RebaseAction.Drop;
    public bool IsCombined => Action is RebaseAction.Squash or RebaseAction.Fixup;
    public bool StopsHere => Action is RebaseAction.Edit or RebaseAction.Reword;

    partial void OnActionChanged(RebaseAction value)
    {
        OnPropertyChanged(nameof(IsDropped));
        OnPropertyChanged(nameof(IsCombined));
        OnPropertyChanged(nameof(StopsHere));
    }
}

/// <summary>
/// The interactive rebase editor: the commits about to be replayed, oldest first, with an action
/// each and the ability to reorder them. Nothing touches the repository until Start.
/// </summary>
public sealed partial class RebaseEditorViewModel(GitRepository repository) : ViewModelBase
{
    /// <summary>Raised once the rebase has run, so the shell can reload.</summary>
    public event EventHandler? RepositoryChanged;

    /// <summary>Set by the view; confirms before rewriting history.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    public ObservableCollection<RebaseRowViewModel> Rows { get; } = [];

    /// <summary>The actions offered per row, in the order they appear in the picker.</summary>
    public static IReadOnlyList<RebaseAction> Actions { get; } =
    [
        RebaseAction.Pick, RebaseAction.Reword, RebaseAction.Edit,
        RebaseAction.Squash, RebaseAction.Fixup, RebaseAction.Drop,
    ];

    [ObservableProperty]
    public partial string Upstream { get; set; } = string.Empty;

    [ObservableProperty]
    public partial RebaseRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    public partial bool HasRun { get; set; }

    public string Title => $"Rebase onto {Upstream}";

    public string Summary => Rows.Count == 1
        ? "1 commit will be replayed"
        : $"{Rows.Count} commits will be replayed";

    /// <summary>Exposed for tests, which need to await the fire-and-forget command body.</summary>
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;

    public async Task LoadAsync(string upstream, CancellationToken ct = default)
    {
        Upstream = upstream;
        OnPropertyChanged(nameof(Title));

        Rows.Clear();
        foreach (var item in await repository.Rebase.GetTodoAsync(upstream, ct).ConfigureAwait(true))
            Rows.Add(new RebaseRowViewModel(item));

        OnPropertyChanged(nameof(Summary));
        SelectedRow = Rows.FirstOrDefault();

        if (Rows.Count == 0)
            StatusText = "There is nothing to rebase — the branch has no commits beyond this point.";

        StartCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------ ordering

    public bool CanMoveUp => SelectedRow is { } row && Rows.IndexOf(row) is > 0;

    public bool CanMoveDown => SelectedRow is not null && Rows.IndexOf(SelectedRow) < Rows.Count - 1;

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int offset)
    {
        if (SelectedRow is not { } row)
            return;

        var from = Rows.IndexOf(row);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Rows.Count)
            return;

        Rows.Move(from, to);

        // Moving must not change which row is selected, or repeated presses would walk away.
        SelectedRow = row;
        NotifyOrderChanged();
    }

    partial void OnSelectedRowChanged(RebaseRowViewModel? value) => NotifyOrderChanged();

    private void NotifyOrderChanged()
    {
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------- running

    public bool CanStart => Rows.Count > 0 && !HasRun;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync()
    {
        PendingOperation = RunAsync();
        return PendingOperation;
    }

    private async Task RunAsync()
    {
        var plan = Rows.Select(r => r.ToItem()).ToList();
        var kept = plan.Count(i => i.Action != RebaseAction.Drop);
        var dropped = plan.Count - kept;

        var message = dropped > 0
            ? $"Rewrite the last {plan.Count} commits, dropping {dropped}? This cannot be undone from here."
            : $"Rewrite the last {plan.Count} commits? This cannot be undone from here.";

        var confirm = ConfirmAsync;
        if (confirm is null || !await confirm(message).ConfigureAwait(true))
            return;

        try
        {
            var result = await repository.Rebase.RunInteractiveAsync(Upstream, plan).ConfigureAwait(true);

            StatusText = result.Message;
            IsError = result.Outcome == OperationOutcome.Failed;

            // A failure changed nothing, so the plan is still worth editing and retrying.
            HasRun = result.Outcome != OperationOutcome.Failed;
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
            IsError = true;
        }
        finally
        {
            StartCommand.NotifyCanExecuteChanged();
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
