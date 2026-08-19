using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// The two shortcuts that open a window, which a KeyBinding cannot express because there is
    /// no command on the view model to bind to.
    /// </summary>
    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        var modifier = OperatingSystem.IsMacOS()
            ? Avalonia.Input.KeyModifiers.Meta
            : Avalonia.Input.KeyModifiers.Control;

        if (!e.KeyModifiers.HasFlag(modifier))
            return;

        switch (e.Key)
        {
            case Avalonia.Input.Key.K:
                OpenPalette();
                e.Handled = true;
                break;

            case Avalonia.Input.Key.R:
                _ = OpenReflogAsync((DataContext as ShellViewModel)?.SelectedRepository);
                e.Handled = true;
                break;

            case Avalonia.Input.Key.OemComma:
                OpenSettings();
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    private void WireUp()
    {
        if (DataContext is not ShellViewModel shell)
            return;

        shell.PickFolderAsync = PickRepositoryFolderAsync;
        shell.ConfigureRepository = ConfigureRepository;
        shell.ApplyTheme = App.ApplyTheme;

        // Repositories opened before the window was wired still need their hooks.
        foreach (var repository in shell.Repositories)
            ConfigureRepository(repository);
    }

    /// <summary>Gives a repository the dialogs only a window can put on screen.</summary>
    private void ConfigureRepository(MainViewModel repository)
    {
        repository.PickFolderAsync = PickRepositoryFolderAsync;
        repository.ConfirmAsync = ConfirmAsync;
        repository.PromptAsync = PromptAsync;
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

    internal async Task OpenReflogAsync(MainViewModel? main)
    {
        if (main?.Repository is not { } repository)
            return;

        var viewModel = new ReflogViewModel(repository)
        {
            ConfirmAsync = ConfirmAsync,
            PromptAsync = PromptAsync,
        };
        viewModel.RepositoryChanged += (_, _) => _ = main.RefreshCommand.ExecuteAsync(null);

        var window = new ReflogWindow { DataContext = viewModel };
        window.Show(this);
        await viewModel.LoadAsync();
    }

    internal async Task OpenRebaseEditorAsync(MainViewModel? main, string upstream)
    {
        if (main?.Repository is not { } repository)
            return;

        var viewModel = new RebaseEditorViewModel(repository) { ConfirmAsync = ConfirmAsync };
        viewModel.RepositoryChanged += (_, _) => _ = main.RefreshCommand.ExecuteAsync(null);

        var window = new RebaseEditorWindow { DataContext = viewModel };
        window.Show(this);
        await viewModel.LoadAsync(upstream);
    }

    internal async Task OpenFileTreeAsync(MainViewModel? main, string revision)
    {
        if (main?.Repository is not { } repository)
            return;

        var viewModel = new FileTreeViewModel(repository);
        var window = new FileTreeWindow { DataContext = viewModel };
        window.Show(this);

        await viewModel.LoadAsync(revision);
    }

    internal async Task OpenIntegrationsAsync(MainViewModel? main)
    {
        if (main?.Repository is not { } repository)
            return;

        var viewModel = new IntegrationsViewModel(repository);
        viewModel.RepositoryChanged += (_, _) => _ = main.RefreshCommand.ExecuteAsync(null);

        var window = new IntegrationsWindow { DataContext = viewModel };
        window.Show(this);

        await viewModel.LoadAsync();
    }

    internal async Task OpenBlameAsync(MainViewModel? main, FileChangeViewModel file, string? revision)
    {
        if (main?.Repository is not { } repository)
            return;

        var viewModel = new BlameViewModel(repository);
        var window = new BlameWindow { DataContext = viewModel };
        window.Show(this);

        await viewModel.LoadAsync(file.Change.Path, revision);
    }

    internal async Task OpenFileHistoryAsync(MainViewModel? main, FileChangeViewModel file)
    {
        if (main?.Repository is not { } repository)
            return;

        var viewModel = new FileHistoryViewModel(repository);
        var window = new FileHistoryWindow { DataContext = viewModel };
        window.Show(this);

        await viewModel.LoadAsync(file.Change.Path);
    }

    /// <summary>
    /// Wires a freshly realised commit detail view to this window, so its file context menu can
    /// open the blame and history windows.
    /// </summary>
    internal void AttachDetailView(CommitDetailView view)
    {
        var main = view.FindAncestorOfType<RepositoryView>()?.DataContext as MainViewModel;
        view.OpenBlameAsync = (file, revision) => OpenBlameAsync(main, file, revision);
        view.OpenFileHistoryAsync = file => OpenFileHistoryAsync(main, file);
    }

    private void OnSettingsClicked(object? sender, RoutedEventArgs e) => _ = OpenSettingsAsync();

    private void OnCommandPaletteClicked(object? sender, RoutedEventArgs e) => OpenPalette();

    /// <summary>Opens settings without awaiting, for callers that cannot.</summary>
    internal void OpenSettings() => _ = OpenSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        if (DataContext is not ShellViewModel shell)
            return;

        var viewModel = new SettingsViewModel(shell.Settings);
        var window = new SettingsWindow { DataContext = viewModel };

        await window.ShowDialog(this);

        if (viewModel.Accepted)
            shell.ApplySettings(viewModel.ToSettings());
    }

    /// <summary>The command palette: every action, searchable, so nothing is mouse-only.</summary>
    internal void OpenPalette()
    {
        if (DataContext is not ShellViewModel shell)
            return;

        var viewModel = new CommandPaletteViewModel(CommandCatalog.Build(shell, this));
        var window = new CommandPaletteWindow { DataContext = viewModel };
        window.Show(this);
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
}
