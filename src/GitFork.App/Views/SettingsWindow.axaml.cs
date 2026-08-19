using Avalonia.Controls;
using GitFork.App.ViewModels;

namespace GitFork.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.CloseRequested += (_, _) => Close();
        };
    }
}
