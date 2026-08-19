using Avalonia.Media.Imaging;

namespace GitGui.App.ViewModels;

/// <summary>
/// An image shown before and after a change. Either side may be missing: a newly added image has
/// no "before", a deleted one has no "after".
/// </summary>
public sealed class ImageDiffViewModel : IDisposable
{
    public Bitmap? Before { get; private init; }
    public Bitmap? After { get; private init; }

    public string BeforeCaption { get; private init; } = string.Empty;
    public string AfterCaption { get; private init; } = string.Empty;

    public bool HasBefore => Before is not null;
    public bool HasAfter => After is not null;

    /// <summary>Exposed so the owner's disposal can be asserted on rather than assumed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Decodes both sides. Bytes that are not a readable image yield a null side rather than
    /// throwing — git will happily store a ".png" that is not one.
    /// </summary>
    public static ImageDiffViewModel Create(byte[]? before, byte[]? after)
    {
        var beforeBitmap = Decode(before);
        var afterBitmap = Decode(after);

        return new ImageDiffViewModel
        {
            Before = beforeBitmap,
            After = afterBitmap,
            BeforeCaption = Describe(before, beforeBitmap, "Not in this revision"),
            AfterCaption = Describe(after, afterBitmap, "Deleted"),
        };
    }

    private static Bitmap? Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Says which of the three things happened: nothing there, bytes that would not decode, or a
    /// real image. Reporting only the byte count would leave a blank pane unexplained.
    /// </summary>
    private static string Describe(byte[]? bytes, Bitmap? bitmap, string missing)
    {
        if (bytes is null || bytes.Length == 0)
            return missing;

        return bitmap is null
            ? $"{bytes.Length:N0} bytes — not a readable image"
            : $"{bitmap.PixelSize.Width}×{bitmap.PixelSize.Height} · {bytes.Length:N0} bytes";
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        IsDisposed = true;
        Before?.Dispose();
        After?.Dispose();
    }
}
