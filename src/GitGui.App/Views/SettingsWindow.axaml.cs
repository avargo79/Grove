using Avalonia.Controls;
using GitGui.App.ViewModels;

namespace GitGui.App.Views;

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
