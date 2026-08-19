using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Grove.App.Tests;

/// <summary>One pixel of a captured frame, in the order a human reads colours.</summary>
public readonly record struct Pixel(byte R, byte G, byte B, byte A)
{
    public int Brightness => (R + G + B) / 3;

    /// <summary>How far apart two colours are; good enough for "did this change at all".</summary>
    public int DistanceTo(Pixel other) =>
        Math.Abs(R - other.R) + Math.Abs(G - other.G) + Math.Abs(B - other.B);

    /// <summary>The gap between the strongest and weakest channel: how vivid the colour is.</summary>
    public int Saturation => Math.Max(R, Math.Max(G, B)) - Math.Min(R, Math.Min(G, B));
}

/// <summary>
/// Reads captured frames.
///
/// The byte order is taken from the locked buffer rather than assumed. Avalonia hands back
/// Rgba8888 on some platforms and Bgra8888 on others, and getting it wrong is close to invisible:
/// brightness, saturation and "did these two regions differ" all survive a red/blue swap, so the
/// mistake only surfaces the day someone compares against an actual colour.
/// </summary>
public static class FramePixels
{
    public static List<Pixel> Read(WriteableBitmap bitmap) =>
        Read(bitmap, new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height));

    public static List<Pixel> Read(WriteableBitmap bitmap, PixelRect region)
    {
        using var buffer = bitmap.Lock();
        var (ri, bi) = buffer.Format == PixelFormat.Rgba8888 ? (0, 2) : (2, 0);

        var left = Math.Max(0, region.X);
        var top = Math.Max(0, region.Y);
        var right = Math.Min(region.Right, buffer.Size.Width);
        var bottom = Math.Min(region.Bottom, buffer.Size.Height);
        var pixels = new List<Pixel>(Math.Max(0, (right - left) * (bottom - top)));

        unsafe
        {
            var scan0 = (byte*)buffer.Address;
            for (var y = top; y < bottom; y++)
            {
                var row = scan0 + (y * buffer.RowBytes);
                for (var x = left; x < right; x++)
                {
                    var p = row + (x * 4);
                    pixels.Add(new Pixel(p[ri], p[1], p[bi], p[3]));
                }
            }
        }

        return pixels;
    }

    /// <summary>Where a control ended up in the window, which is where its pixels are.</summary>
    public static PixelRect BoundsOf(Visual control, Visual root)
    {
        var topLeft = control.TranslatePoint(default, root) ?? default;
        return new PixelRect(
            (int)topLeft.X, (int)topLeft.Y,
            (int)control.Bounds.Width, (int)control.Bounds.Height);
    }

    public static Pixel Average(this List<Pixel> pixels) => pixels.Count == 0
        ? default
        : new Pixel(
            (byte)pixels.Average(p => p.R),
            (byte)pixels.Average(p => p.G),
            (byte)pixels.Average(p => p.B),
            255);
}
