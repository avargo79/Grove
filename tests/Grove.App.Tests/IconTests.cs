using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Xunit;

namespace Grove.App.Tests;

/// <summary>
/// The icon is code, so it is worth checking what it actually rasterises to. A visual-tree
/// assertion would prove nothing here: there is no tree, only pixels.
/// </summary>
public class IconTests
{
    /// <summary>Renders the mark and decodes it back, which exercises the PNG the scripts pack.</summary>
    private static WriteableBitmap Rasterise(int size)
    {
        using var bitmap = IconGenerator.RenderIcon(size);
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return WriteableBitmap.Decode(stream);
    }

    private readonly record struct Pixel(byte B, byte G, byte R, byte A);

    private static List<Pixel> ReadPixels(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var pixels = new List<Pixel>(buffer.Size.Width * buffer.Size.Height);

        // A decoded PNG comes back in whichever byte order the platform decoder prefers, so the
        // channel offsets are read from the buffer rather than assumed. Getting this wrong is
        // invisible in a green-dominant image: only the exact colour comparisons notice.
        var rgba = buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        var (ri, bi) = rgba ? (0, 2) : (2, 0);

        unsafe
        {
            var scan0 = (byte*)buffer.Address;
            for (var y = 0; y < buffer.Size.Height; y++)
            {
                var row = scan0 + (y * buffer.RowBytes);
                for (var x = 0; x < buffer.Size.Width; x++)
                {
                    var p = row + (x * 4);
                    pixels.Add(new Pixel(p[bi], p[1], p[ri], p[3]));
                }
            }
        }

        return pixels;
    }

    /// <summary>Foliage: clearly green, and clearly not one of the slate background tones.</summary>
    private static bool IsFoliage(Pixel p) => p.A > 200 && p.G > 90 && p.G - p.R > 30 && p.G - p.B > 30;

    [AvaloniaTheory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(256)]
    [InlineData(1024)]
    public void TheIconRendersAtEverySizeThePlatformsAskFor(int size)
    {
        using var bitmap = Rasterise(size);

        Assert.Equal(size, bitmap.PixelSize.Width);
        Assert.Equal(size, bitmap.PixelSize.Height);
        Assert.True(ReadPixels(bitmap).Distinct().Count() > 3, "the icon rendered as a flat fill");
    }

    [AvaloniaTheory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(256)]
    public void TheTreesStayLegibleDownToTheSmallestSize(int size)
    {
        var pixels = ReadPixels(Rasterise(size));
        var foliage = pixels.Count(IsFoliage) / (double)pixels.Count;

        // A grove that survives downsampling to 16px has to be mostly canopy: at that size a
        // trunk is a pixel wide, so green area is the whole of what the eye gets.
        Assert.True(foliage > 0.15, $"only {foliage:P0} of the {size}px icon is foliage");
    }

    [AvaloniaFact]
    public void TheCornersAreTransparentSoThePlatformMaskFits()
    {
        const int size = 256;
        var pixels = ReadPixels(Rasterise(size));

        Assert.Equal(0, pixels[0].A);
        Assert.Equal(0, pixels[size - 1].A);
        Assert.Equal(0, pixels[^1].A);
        Assert.Equal(0, pixels[^size].A);

        // The rounding is a corner treatment, not a circle: the edge midpoints stay solid.
        Assert.Equal(255, pixels[size / 2].A);
        Assert.Equal(255, pixels[(size / 2 * size) + size - 1].A);
    }

    [AvaloniaFact]
    public void TheGroveIsThreeTreesInDepthRatherThanOneMass()
    {
        const int size = 256;
        var pixels = ReadPixels(Rasterise(size));

        // The three canopy tones are what separate the trees; the outer two deliberately touch
        // the middle one, so counting silhouettes would only measure how far apart they sit.
        (byte R, byte G, byte B)[] canopies =
        [
            (0x7C, 0xD0, 0x8F),
            (0x4F, 0xA9, 0x6A),
            (0x2F, 0x7A, 0x4C),
        ];

        foreach (var (r, g, b) in canopies)
        {
            var share = pixels.Count(p =>
                p.A > 200 && Math.Abs(p.R - r) < 8 && Math.Abs(p.G - g) < 8 && Math.Abs(p.B - b) < 8)
                / (double)pixels.Count;

            Assert.True(share > 0.02, $"tone #{r:X2}{g:X2}{b:X2} covers only {share:P1}");
        }
    }

    [AvaloniaFact]
    public void TheMiddleTreeIsTheTallest()
    {
        const int size = 256;
        var pixels = ReadPixels(Rasterise(size));

        var top = Enumerable.Range(0, size)
            .First(y => Enumerable.Range(0, size).Any(x => IsFoliage(pixels[(y * size) + x])));

        var columns = Enumerable.Range(0, size).Where(x => IsFoliage(pixels[(top * size) + x])).ToList();
        var centre = columns.Average() / size;

        // The crown of the grove sits over the middle tree, which is what gives the mark a peak
        // instead of a flat hedge.
        Assert.InRange(centre, 0.45, 0.55);
    }

    [AvaloniaFact]
    public void EveryWindowCarriesTheIcon()
    {
        var (window, _) = TestShell.Empty();

        Assert.NotNull(window.Icon);
    }
}
