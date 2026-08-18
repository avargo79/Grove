using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GitFork.App.ViewModels;
using GitFork.App.Views;

namespace GitFork.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Opening from a path argument, or from the directory the app was launched in,
            // means "gitfork ." behaves the way the rest of the git tooling does.
            var startPath = desktop.Args is [var first, ..] ? first : Directory.GetCurrentDirectory();
            _ = viewModel.LoadRepositoryAsync(startPath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}