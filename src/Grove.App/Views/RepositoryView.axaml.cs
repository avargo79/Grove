using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Grove.App.ViewModels;

namespace Grove.App.Views;

/// <summary>
/// One repository's entire UI. Extracted from the window so several can be open at once as tabs;
/// the window supplies the dialog hooks, since only it can parent a modal.
/// </summary>
public partial class RepositoryView : UserControl
{
    public RepositoryView() => InitializeComponent();

    private MainWindow? Host => this.FindAncestorOfType<MainWindow>();

    /// <summary>Enter searches from any of the filter boxes, which is what a search box implies.</summary>
    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel vm)
            return;

        e.Handled = true;
        _ = vm.ApplyFilterCommand.ExecuteAsync(null);
    }

    /// <summary>Selecting the pinned row hands the lower pane to the working copy.</summary>
    private void OnUncommittedRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SelectWorkingCopy();
    }

    /// <summary>Clicking a branch, tag or stash jumps the commit list to the ref's tip.</summary>
    private void OnSidebarSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        if (sender is not ListBox { SelectedItem: SidebarItemViewModel item })
            return;

        vm.SelectCommitBySha(item.TargetSha);

        var list = this.FindControl<ListBox>("CommitList");
        if (list is not null && vm.SelectedCommit is { } selected)
            list.ScrollIntoView(selected);
    }

    private void OnFilesClicked(object? sender, RoutedEventArgs e) =>
        _ = Host?.OpenFileTreeAsync(DataContext as MainViewModel, "HEAD") ?? Task.CompletedTask;

    private void OnIntegrationsClicked(object? sender, RoutedEventArgs e) =>
        _ = Host?.OpenIntegrationsAsync(DataContext as MainViewModel) ?? Task.CompletedTask;

    /// <summary>Browsing the tree as it was at one commit, rather than as it is now.</summary>
    private void OnBrowseFilesAtCommitClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: CommitRowViewModel row })
            _ = Host?.OpenFileTreeAsync(DataContext as MainViewModel, row.Sha);
    }

    private void OnReflogClicked(object? sender, RoutedEventArgs e) =>
        _ = Host?.OpenReflogAsync(DataContext as MainViewModel) ?? Task.CompletedTask;

    /// <summary>Rebase onto the ref that was right-clicked in the sidebar.</summary>
    private void OnInteractiveRebaseFromRefClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarItemViewModel item })
            _ = Host?.OpenRebaseEditorAsync(DataContext as MainViewModel, item.Ref.ShortName);
    }

    /// <summary>
    /// Rebase the commits above the one that was right-clicked, which is what "from here" means:
    /// that commit becomes the upstream everything after it is replayed onto.
    /// </summary>
    private void OnInteractiveRebaseFromCommitClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: CommitRowViewModel row })
            _ = Host?.OpenRebaseEditorAsync(DataContext as MainViewModel, row.Sha);
    }
}
