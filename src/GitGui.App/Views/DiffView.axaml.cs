using Avalonia.Controls;
using Avalonia.Interactivity;
using GitGui.App.ViewModels;
using GitGui.Core;

namespace GitGui.App.Views;

public partial class DiffView : UserControl
{
    public DiffView() => InitializeComponent();

    private void OnUnifiedChecked(object? sender, RoutedEventArgs e) =>
        SetMode(sender, DiffViewMode.Unified);

    private void OnSideBySideChecked(object? sender, RoutedEventArgs e) =>
        SetMode(sender, DiffViewMode.SideBySide);

    /// <summary>Only the button being turned *on* decides the mode; the other one is just clearing.</summary>
    private void SetMode(object? sender, DiffViewMode mode)
    {
        if (DataContext is DiffViewModel vm && sender is RadioButton { IsChecked: true })
            vm.Mode = mode;
    }

    /// <summary>The combo's order matches the WhitespaceMode values, so the index maps directly.</summary>
    private void OnWhitespaceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not DiffViewModel vm || sender is not ComboBox box)
            return;

        vm.Whitespace = box.SelectedIndex switch
        {
            1 => WhitespaceMode.IgnoreChange,
            2 => WhitespaceMode.IgnoreAll,
            3 => WhitespaceMode.IgnoreBlankLines,
            _ => WhitespaceMode.Show,
        };
    }
}
