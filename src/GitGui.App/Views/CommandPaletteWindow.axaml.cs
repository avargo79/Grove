using Avalonia.Controls;
using Avalonia.Input;
using GitGui.App.ViewModels;

namespace GitGui.App.Views;

public partial class CommandPaletteWindow : Window
{
    public CommandPaletteWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is CommandPaletteViewModel vm)
                vm.CloseRequested += (_, _) => Close();
        };

        // The query box keeps focus; the arrow keys drive the list from there so the palette can
        // be used without ever leaving the keyboard.
        Opened += (_, _) => this.FindControl<TextBox>("PaletteQuery")?.Focus();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandPaletteViewModel vm)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Enter:
                vm.InvokeCommand.Execute(vm.SelectedCommand);
                e.Handled = true;
                break;

            case Key.Down:
                Move(vm, 1);
                e.Handled = true;
                break;

            case Key.Up:
                Move(vm, -1);
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    private static void Move(CommandPaletteViewModel vm, int offset)
    {
        if (vm.Commands.Count == 0)
            return;

        var index = vm.SelectedCommand is null ? -1 : vm.Commands.IndexOf(vm.SelectedCommand);
        vm.SelectedCommand = vm.Commands[Math.Clamp(index + offset, 0, vm.Commands.Count - 1)];
    }
}
