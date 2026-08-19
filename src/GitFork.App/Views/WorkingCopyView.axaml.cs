using Avalonia.Controls;
using GitFork.App.ViewModels;

namespace GitFork.App.Views;

public partial class WorkingCopyView : UserControl
{
    public WorkingCopyView() => InitializeComponent();

    /// <summary>The two file lists are one logical selection, so picking in one clears the other.</summary>
    private void OnStagedSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        SyncFileSelection(sender, otherListName: "UnstagedList");

    private void OnUnstagedSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        SyncFileSelection(sender, otherListName: "StagedList");

    private void SyncFileSelection(object? sender, string otherListName)
    {
        if (DataContext is not WorkingCopyViewModel vm)
            return;
        if (sender is not ListBox { SelectedItem: WorkingFileViewModel file })
            return;

        this.FindControl<ListBox>(otherListName)?.UnselectAll();
        vm.SelectedFile = file;
    }

    /// <summary>Mirrors the diff's multi-selection into the view model for line-level staging.</summary>
    private void OnDiffSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not WorkingCopyViewModel vm || sender is not ListBox list)
            return;

        vm.SelectedRows.Clear();
        foreach (var item in list.SelectedItems?.OfType<DiffRowViewModel>() ?? [])
            vm.SelectedRows.Add(item);

        vm.NotifySelectionChanged();
    }
}
