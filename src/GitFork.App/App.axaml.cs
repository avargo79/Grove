using System.IO;
using Avalonia;
using Avalonia.Styling;
using GitFork.Core;
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

    /// <summary>Switches the whole app's palette. Every theme brush is resolved dynamically.</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        if (Current is not null)
            Current.RequestedThemeVariant = theme == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new ShellViewModel();
            ApplyTheme(shell.Settings.Theme);

            desktop.MainWindow = new MainWindow { DataContext = shell };

            // Opening from a path argument, or from the directory the app was launched in,
            // means "gitfork ." behaves the way the rest of the git tooling does.
            var startPath = desktop.Args is [var first, ..] ? first : Directory.GetCurrentDirectory();
            _ = shell.OpenAsync(startPath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}