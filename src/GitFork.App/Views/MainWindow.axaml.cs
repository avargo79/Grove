using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GitFork.App.ViewModels;

namespace GitFork.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireUp();
        WireUp();
    }

    private void WireUp()
    {
        if (DataContext is MainViewModel vm)
            vm.PickFolderAsync = PickRepositoryFolderAsync;
    }

    /// <summary>Native folder picker; returns the chosen path or null if the user cancelled.</summary>
    private async Task<string?> PickRepositoryFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Repository",
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
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
}
