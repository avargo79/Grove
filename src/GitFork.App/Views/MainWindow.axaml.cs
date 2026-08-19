using Avalonia.Controls;
using Avalonia.Input;
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
        if (DataContext is not MainViewModel vm)
            return;

        vm.PickFolderAsync = PickRepositoryFolderAsync;
        vm.ConfirmAsync = ConfirmAsync;
        vm.PromptAsync = PromptAsync;
    }

    /// <summary>
    /// Wires a freshly realised commit detail view to this window, so its file context menu can
    /// open the blame and history windows.
    /// </summary>
    internal void AttachDetailView(CommitDetailView view)
    {
        view.OpenBlameAsync = OpenBlameAsync;
        view.OpenFileHistoryAsync = OpenFileHistoryAsync;
    }

    private async Task OpenBlameAsync(ViewModels.FileChangeViewModel file, string? revision)
    {
        if (DataContext is not MainViewModel { Repository: { } repository })
            return;

        var viewModel = new ViewModels.BlameViewModel(repository);
        var window = new BlameWindow { DataContext = viewModel };
        window.Show(this);

        await viewModel.LoadAsync(file.Change.Path, revision);
    }

    private async Task OpenFileHistoryAsync(ViewModels.FileChangeViewModel file)
    {
        if (DataContext is not MainViewModel { Repository: { } repository })
            return;

        var viewModel = new ViewModels.FileHistoryViewModel(repository);
        var window = new FileHistoryWindow { DataContext = viewModel };
        window.Show(this);

        await viewModel.LoadAsync(file.Change.Path);
    }

    /// <summary>Selecting the pinned row hands the lower pane to the working copy.</summary>
    private void OnUncommittedRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SelectWorkingCopy();
    }

    /// <summary>Modal single-line text prompt. Returns null when the user cancels.</summary>
    private async Task<string?> PromptAsync(PromptRequest request)
    {
        string? value = null;

        var input = new TextBox { Text = request.InitialValue };
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        var dialog = new Window
        {
            Title = request.Title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        ok.Click += (_, _) => { value = input.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = request.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                input,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };

        dialog.Opened += (_, _) => input.Focus();
        await dialog.ShowDialog(this);
        return value;
    }

    /// <summary>Modal yes/no used before anything destructive, such as discarding changes.</summary>
    private async Task<bool> ConfirmAsync(string message)
    {
        var confirmed = false;

        var yes = new Button { Content = "Yes, continue", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var no = new Button { Content = "Cancel", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };

        var dialog = new Window
        {
            Title = "Confirm",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        yes.Click += (_, _) => { confirmed = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { no, yes },
                },
            },
        };

        await dialog.ShowDialog(this);
        return confirmed;
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
