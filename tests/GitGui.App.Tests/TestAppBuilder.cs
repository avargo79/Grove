using Avalonia;
using Avalonia.Headless;
using GitGui.App;

[assembly: AvaloniaTestApplication(typeof(GitGui.App.Tests.TestAppBuilder))]

namespace GitGui.App.Tests;

/// <summary>
/// Boots the real <see cref="App"/> against Avalonia's headless platform, so UI tests exercise the
/// actual XAML, styles and custom controls without needing a display.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            // Real Skia rendering rather than the no-op drawing backend, so tests can capture
            // and inspect the actual pixels the app would put on screen.
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
