using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitFork.Core;

namespace GitFork.App.ViewModels;

/// <summary>
/// The settings dialog. Edits a copy so cancelling really cancels; only <see cref="Accepted"/>
/// tells the shell to take the values.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(AppSettings settings)
    {
        IsLightTheme = settings.Theme == AppTheme.Light;
        DiffContextLines = settings.DiffContextLines;
        WhitespaceIndex = (int)settings.DiffWhitespace;
        ShowSyntaxHighlighting = settings.ShowSyntaxHighlighting;
        ShowWordHighlighting = settings.ShowWordHighlighting;
        CommitPageSize = settings.CommitPageSize;
        _original = settings;
    }

    private readonly AppSettings _original;

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    public bool Accepted { get; private set; }

    [ObservableProperty]
    public partial bool IsLightTheme { get; set; }

    [ObservableProperty]
    public partial int DiffContextLines { get; set; }

    [ObservableProperty]
    public partial int WhitespaceIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowSyntaxHighlighting { get; set; }

    [ObservableProperty]
    public partial bool ShowWordHighlighting { get; set; }

    [ObservableProperty]
    public partial int CommitPageSize { get; set; }

    public AppSettings ToSettings() => _original with
    {
        Theme = IsLightTheme ? AppTheme.Light : AppTheme.Dark,
        DiffContextLines = Math.Clamp(DiffContextLines, 0, 50),
        DiffWhitespace = (WhitespaceMode)Math.Clamp(WhitespaceIndex, 0, 3),
        ShowSyntaxHighlighting = ShowSyntaxHighlighting,
        ShowWordHighlighting = ShowWordHighlighting,

        // A page size of zero would load nothing at all and look like a broken repository.
        CommitPageSize = Math.Clamp(CommitPageSize, 50, 20000),
    };

    [RelayCommand]
    private void Save()
    {
        Accepted = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Accepted = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
