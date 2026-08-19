using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Grove.App.Controls;
using Xunit;

namespace Grove.App.Tests;

/// <summary>
/// Renders <see cref="GroveIcon"/> to PNG at whatever size is asked for. Not a test: it writes
/// the icon sources that scripts/make-icons.sh packs into .ico and .icns.
///
///     GROVE_ICON_DIR=build/icon dotnet test tests/Grove.App.Tests --filter WriteIcon
/// </summary>
public class IconGenerator
{
    /// <summary>Every size Windows and macOS ask for, plus the in-app window icon.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024];

    [AvaloniaFact]
    public void WriteIcon()
    {
        var directory = Environment.GetEnvironmentVariable("GROVE_ICON_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return;

        Directory.CreateDirectory(directory);

        foreach (var size in Sizes)
        {
            var path = Path.Combine(directory, $"icon-{size}.png");
            using (var bitmap = RenderIcon(size))
            using (var stream = File.Create(path))
                bitmap.Save(stream, new PngBitmapEncoderOptions());

            // Checked after the stream closes: an open writer still reports a length of zero.
            Assert.True(new FileInfo(path).Length > 0);
        }
    }

    /// <summary>Rasterises the mark at one size. Shared with the tests that inspect the pixels.</summary>
    internal static RenderTargetBitmap RenderIcon(int size)
    {
        var icon = new GroveIcon { Width = size, Height = size };
        icon.Measure(new Size(size, size));
        icon.Arrange(new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(icon);
        return bitmap;
    }
}
