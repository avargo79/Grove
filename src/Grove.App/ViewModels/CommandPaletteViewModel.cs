using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Grove.App.ViewModels;

/// <summary>One action the palette can run.</summary>
public sealed record PaletteCommand(string Name, string Category, string Shortcut, Func<Task> Run)
{
    /// <summary>Matched against a typed query, over both the name and its category.</summary>
    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Category.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A searchable list of every action, so nothing in the app is reachable only by mouse and the
/// keyboard shortcuts are discoverable rather than folklore.
/// </summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly IReadOnlyList<PaletteCommand> _all;

    public CommandPaletteViewModel(IReadOnlyList<PaletteCommand> commands)
    {
        _all = commands;
        Filter();
    }

    /// <summary>Raised when the palette should close.</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<PaletteCommand> Commands { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PaletteCommand? SelectedCommand { get; set; }

    /// <summary>Exposed for tests, which need to await the fire-and-forget invocation.</summary>
    internal Task PendingInvocation { get; private set; } = Task.CompletedTask;

    public bool HasResults => Commands.Count > 0;

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        Commands.Clear();
        foreach (var command in _all.Where(c => string.IsNullOrWhiteSpace(Query) || c.Matches(Query.Trim())))
            Commands.Add(command);

        OnPropertyChanged(nameof(HasResults));

        // Enter should always have something to run, so keep a selection where possible.
        SelectedCommand = Commands.FirstOrDefault();
    }

    [RelayCommand]
    private void Invoke(PaletteCommand? command)
    {
        var target = command ?? SelectedCommand;
        if (target is null)
            return;

        CloseRequested?.Invoke(this, EventArgs.Empty);
        PendingInvocation = target.Run();
    }
}
