using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitGui.App.ViewModels;

namespace GitGui.App.Views;

public partial class CommitDetailView : UserControl
{
    public CommitDetailView() => InitializeComponent();

    /// <summary>
    /// Set by whoever hosts this view, so the detail pane does not need to know how windows are
    /// opened. Left null in tests that only care about the diff.
    /// </summary>
    public Func<FileChangeViewModel, string?, Task>? OpenBlameAsync { get; set; }

    public Func<FileChangeViewModel, Task>? OpenFileHistoryAsync { get; set; }

    private void OnBlameClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedFile() is { } file && OpenBlameAsync is { } open)
            _ = open(file, (DataContext as CommitDetailViewModel)?.Sha);
    }

    private void OnFileHistoryClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedFile() is { } file && OpenFileHistoryAsync is { } open)
            _ = open(file);
    }

    /// <summary>
    /// ViewLocator creates this view, so it finds its host window itself rather than being handed
    /// the hooks by whoever created it.
    /// </summary>
    private void OnAttachedToTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (this.FindAncestorOfType<MainWindow>() is { } window)
            window.AttachDetailView(this);
    }

    private FileChangeViewModel? SelectedFile() =>
        (DataContext as CommitDetailViewModel)?.SelectedFile;
}
