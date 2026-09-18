using SkiaSharp;

namespace Photo2CullNet.Core.Imaging;

/// <summary>
/// The only place SkiaSharp is referenced directly: decoding standard image
/// formats, resizing, and encoding thumbnails back out for the UI. Kept
/// separate from <see cref="ImageOps"/> so the scoring math has no codec
/// dependency.
/// </summary>
public static class SkiaImageCodec
{
    /// <summary>
    /// Decode a standard-format image (PNG/JPEG/TIFF/BMP/WebP) to RGB8, with
    /// EXIF orientation applied, downsized so neither edge exceeds
    /// <paramref name="maxDim"/>.
    /// </summary>
    public static RgbImage DecodeStandard(string path, int maxDim)
    {
        using var codec = SKCodec.Create(path)
            ?? throw new InvalidOperationException($"unrecognized image format: {path}");
        using var original = SKBitmap.Decode(codec);
        if (original is null)
        {
            throw new InvalidOperationException($"failed to decode {path}");
        }

        using var oriented = ApplyOrientation(original, codec.EncodedOrigin);
        var rgb = ToRgbImage(oriented);
        var (w, h) = FitDimensions(rgb.Width, rgb.Height, maxDim);
        return ImageOps.ResizeTriangle(rgb, w, h);
    }

    /// <summary>Resize (fit within a box, preserving aspect ratio).</summary>
    public static RgbImage ResizeToFit(RgbImage img, int maxDim)
    {
        var (w, h) = FitDimensions(img.Width, img.Height, maxDim);
        return ImageOps.ResizeTriangle(img, w, h);
    }

    /// <summary>Stretch-resize to an exact size (used for the 9x8 dHash step and the face detector's 320x240 input).</summary>
    public static RgbImage ResizeExactTo(RgbImage img, int w, int h) => ImageOps.ResizeTriangle(img, w, h);

    /// <summary>Encode to PNG bytes, for embedding thumbnails in the UI.</summary>
    public static byte[] EncodePng(RgbImage img)
    {
        using var bmp = ToSkBitmap(img);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (int W, int H) FitDimensions(int w, int h, int maxDim)
    {
        if (w <= maxDim && h <= maxDim)
        {
            return (w, h);
        }
        double scale = Math.Min((double)maxDim / w, (double)maxDim / h);
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return bitmap;
        }

        bool swapsDimensions = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.LeftBottom or SKEncodedOrigin.RightBottom;
        var rotated = new SKBitmap(swapsDimensions ? bitmap.Height : bitmap.Width,
            swapsDimensions ? bitmap.Width : bitmap.Height, bitmap.ColorType, bitmap.AlphaType);

        using var canvas = new SKCanvas(rotated);
        var matrix = OrientationMatrix(origin, bitmap.Width, bitmap.Height);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(bitmap, 0, 0);
        bitmap.Dispose();
        return rotated;
    }

    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => SKMatrix.CreateScale(-1, 1).PostConcat(SKMatrix.CreateTranslation(w, 0)),
        SKEncodedOrigin.BottomRight => SKMatrix.CreateRotationDegrees(180, w / 2f, h / 2f),
        SKEncodedOrigin.BottomLeft => SKMatrix.CreateScale(1, -1).PostConcat(SKMatrix.CreateTranslation(0, h)),
        SKEncodedOrigin.LeftTop => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateScale(-1, 1)),
        SKEncodedOrigin.RightTop => SKMatrix.CreateRotationDegrees(90, w / 2f, h / 2f)
            .PostConcat(SKMatrix.CreateTranslation((h - w) / 2f, (w - h) / 2f)),
        SKEncodedOrigin.RightBottom => SKMatrix.CreateRotationDegrees(-90).PostConcat(SKMatrix.CreateScale(-1, 1))
            .PostConcat(SKMatrix.CreateTranslation(h, w)),
        SKEncodedOrigin.LeftBottom => SKMatrix.CreateRotationDegrees(-90, w / 2f, h / 2f)
            .PostConcat(SKMatrix.CreateTranslation((h - w) / 2f, (w - h) / 2f)),
        _ => SKMatrix.Identity,
    };

    /// <summary>
    /// Converts via the bitmap's raw pixel buffer rather than per-pixel
    /// <c>GetPixel</c> calls -- meaningfully faster at the 1600px score /
    /// 220px thumbnail sizes this app decodes at.
    /// </summary>
    public static RgbImage ToRgbImage(SKBitmap bitmap)
    {
        using var rgba = bitmap.Copy(SKColorType.Rgba8888);
        var packed = rgba.Bytes; // RGBA, 4 bytes/pixel, row-major
        var img = new RgbImage(bitmap.Width, bitmap.Height);
        var dst = img.Pixels;
        int n = bitmap.Width * bitmap.Height;
        for (int p = 0, s = 0, d = 0; p < n; p++, s += 4, d += 3)
        {
            dst[d] = packed[s];
            dst[d + 1] = packed[s + 1];
            dst[d + 2] = packed[s + 2];
        }
        return img;
    }

    public static SKBitmap ToSkBitmap(RgbImage img)
    {
        var src = img.Pixels;
        int n = img.Width * img.Height;
        var packed = new byte[n * 4];
        for (int p = 0, s = 0, d = 0; p < n; p++, s += 3, d += 4)
        {
            packed[d] = src[s];
            packed[d + 1] = src[s + 1];
            packed[d + 2] = src[s + 2];
            packed[d + 3] = 255;
        }

        var bmp = new SKBitmap(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        System.Runtime.InteropServices.Marshal.Copy(packed, 0, bmp.GetPixels(), packed.Length);
        return bmp;
    }
}
