using Photo2CullNet.Core.Imaging;
using Photo2CullNet.Core.Raw;

namespace Photo2CullNet.Core;

/// <summary>Photo format dispatch, ported from <c>photo.rs</c>.</summary>
public static class Photo
{
    /// <summary>
    /// Standard (already-demosaiced) image formats, matched case-
    /// insensitively. HEIC/HEIF isn't included: it needs H.265/HEVC
    /// decoding, which isn't reliably available across all three target
    /// platforms without extra native codecs.
    /// </summary>
    public static readonly string[] StandardExtensions = ["png", "jpg", "jpeg", "tif", "tiff", "bmp", "webp"];

    public static bool IsStandardImage(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return StandardExtensions.Contains(ext);
    }

    /// <summary>Any photo format this app can decode -- RAW or standard.</summary>
    public static bool IsSupportedPhoto(string path) => RawPhoto.IsRawFile(path) || IsStandardImage(path);

    /// <summary>
    /// Decode any supported photo (RAW or standard format) to RGB8,
    /// downsized so neither edge exceeds <paramref name="maxDim"/>.
    /// </summary>
    public static RgbImage DecodePhoto(string path, int maxDim) =>
        RawPhoto.IsRawFile(path) ? RawPhoto.Decode(path, maxDim) : SkiaImageCodec.DecodeStandard(path, maxDim);
}
