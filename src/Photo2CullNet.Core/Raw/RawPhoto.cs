using System.Runtime.InteropServices;
using Photo2CullNet.Core.Imaging;
using Sdcb.LibRaw;

namespace Photo2CullNet.Core.Raw;

/// <summary>
/// RAW file support, ported from <c>raw.rs</c>.
///
/// Platform gap: the only maintained .NET LibRaw binding
/// (<c>Sdcb.LibRaw</c>) only ships native binaries for Windows x64 and
/// Ubuntu 22.04 x64 -- there is no macOS package upstream. RAW decoding
/// therefore throws <see cref="PlatformNotSupportedException"/> on macOS
/// for now; a native ImageIO/Core Image-based backend (which has excellent
/// built-in RAW support on macOS, no LibRaw needed) is the natural
/// follow-up, kept behind <see cref="IsSupported"/> so callers can check
/// before offering RAW files rather than failing per-file.
/// </summary>
public static class RawPhoto
{
    public static readonly string[] RawExtensions =
    [
        "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "orf", "rw2",
        "raf", "dng", "pef", "ptx", "srw", "x3f", "3fr", "erf", "kdc", "mrw",
        "raw", "mos", "iiq", "rwl", "dcr",
    ];

    public static bool IsRawFile(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return RawExtensions.Contains(ext);
    }

    /// <summary>Whether RAW decoding is available on the current OS/architecture.</summary>
    public static bool IsSupported =>
        (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// Decode a RAW file to RGB8, downsized so neither edge exceeds
    /// <paramref name="maxDim"/>.
    ///
    /// Rather than demosaicing the sensor's CFA data, this extracts the
    /// camera's embedded preview (every RAW file carries at least one, for
    /// the camera's own rear-LCD display and for fast previews in other
    /// tools) -- see raw.rs's comment for why that sidesteps per-camera
    /// demosaic/calibration support entirely. Falls back to a full
    /// (slower) demosaic only if no usable embedded preview is found.
    /// </summary>
    public static RgbImage Decode(string path, int maxDim)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                $"RAW decoding isn't supported on this platform yet ({RuntimeInformation.OSDescription}, "
                + $"{RuntimeInformation.ProcessArchitecture}) -- no LibRaw native binary is available for it. "
                + $"Affected file: {path}");
        }

        using RawContext ctx = RawContext.OpenFile(path);

        using ProcessedImage? thumb = TryExportThumbnail(ctx);
        if (thumb is not null)
        {
            var decoded = TryDecodeAsImage(thumb) ?? RawBitmapToRgb(thumb);
            return SkiaImageCodec.ResizeToFit(decoded, maxDim);
        }

        // No usable embedded preview -- fall back to full demosaic.
        ctx.Unpack();
        ctx.DcrawProcess();
        using ProcessedImage full = ctx.MakeDcrawMemoryImage();
        return SkiaImageCodec.ResizeToFit(RawBitmapToRgb(full), maxDim);
    }

    private static ProcessedImage? TryExportThumbnail(RawContext ctx)
    {
        try
        {
            return ctx.ExportThumbnail(thumbnailIndex: 0);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Most cameras store their embedded preview as a JPEG -- try decoding
    /// the thumbnail bytes as a standard compressed image first (this also
    /// picks up its own EXIF orientation for free). Returns null if the
    /// bytes aren't a recognizable compressed format, so the caller falls
    /// back to treating them as a raw RGB24 bitmap.
    /// </summary>
    private static RgbImage? TryDecodeAsImage(ProcessedImage thumb)
    {
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(thumb.AsSpan<byte>().ToArray()));
            if (codec is null) return null;
            using var bitmap = SkiaSharp.SKBitmap.Decode(codec);
            return bitmap is null ? null : SkiaImageCodec.ToRgbImage(bitmap);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Interprets the image's bytes as a raw RGB24 bitmap (LibRaw's default output format).</summary>
    private static RgbImage RawBitmapToRgb(ProcessedImage img)
    {
        var bytes = img.AsSpan<byte>().ToArray();
        return new RgbImage(img.Width, img.Height, bytes);
    }
}
