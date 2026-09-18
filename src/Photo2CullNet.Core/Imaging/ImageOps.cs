namespace Photo2CullNet.Core.Imaging;

/// <summary>
/// Pure pixel-array operations ported from the Rust app's use of the
/// <c>image</c>/<c>imageproc</c> crates: grayscale conversion, cropping and
/// the Laplacian filter used for sharpness/composition scoring. Kept free
/// of any image-codec dependency (SkiaSharp lives in <see cref="SkiaImageCodec"/>
/// instead) so this stays trivial to unit test.
/// </summary>
public static class ImageOps
{
    /// <summary>
    /// BT.709 luma weights, matching the <c>image</c> crate's
    /// <c>Rgb&lt;u8&gt;::to_luma</c>.
    /// </summary>
    public static GrayImage ToGray(RgbImage rgb)
    {
        var gray = new GrayImage(rgb.Width, rgb.Height);
        var src = rgb.Pixels;
        var dst = gray.Pixels;
        for (int i = 0, p = 0; p < dst.Length; i += 3, p++)
        {
            double l = 0.2126 * src[i] + 0.7152 * src[i + 1] + 0.0722 * src[i + 2];
            dst[p] = (byte)Math.Clamp(Math.Round(l), 0, 255);
        }
        return gray;
    }

    /// <summary>Crop, clamped to the image bounds.</summary>
    public static GrayImage Crop(GrayImage img, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Math.Max(img.Width - 1, 0));
        y = Math.Clamp(y, 0, Math.Max(img.Height - 1, 0));
        w = Math.Max(Math.Min(w, img.Width - x), 1);
        h = Math.Max(Math.Min(h, img.Height - y), 1);

        var result = new GrayImage(w, h);
        for (int row = 0; row < h; row++)
        {
            Array.Copy(img.Pixels, (y + row) * img.Width + x, result.Pixels, row * w, w);
        }
        return result;
    }

    /// <summary>
    /// Crop a centered region covering <paramref name="fraction"/> of each
    /// axis (1.0 = the whole frame).
    /// </summary>
    public static GrayImage CenterCrop(GrayImage img, float fraction)
    {
        if (fraction >= 1.0f)
        {
            return img;
        }

        int cw = Math.Max((int)Math.Round(img.Width * fraction), 1);
        int ch = Math.Max((int)Math.Round(img.Height * fraction), 1);
        int x = (img.Width - cw) / 2;
        int y = (img.Height - ch) / 2;
        return Crop(img, x, y, cw, ch);
    }

    /// <summary>
    /// 3x3 Laplacian edge filter (kernel [[-1,-1,-1],[-1,8,-1],[-1,-1,-1]]),
    /// matching <c>imageproc::filter::laplacian_filter</c>'s kernel. Border
    /// pixels are handled by clamping (replicating the edge), the standard
    /// convention for this kind of convolution -- imageproc's own exact
    /// border handling wasn't verified against this port, so treat absolute
    /// values near the frame edge as an approximation.
    /// </summary>
    public static short[] Laplacian(GrayImage img)
    {
        int w = img.Width, h = img.Height;
        var src = img.Pixels;
        var dst = new short[w * h];

        int At(int x, int y)
        {
            x = Math.Clamp(x, 0, w - 1);
            y = Math.Clamp(y, 0, h - 1);
            return src[y * w + x];
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 8 * At(x, y)
                    - At(x - 1, y - 1) - At(x, y - 1) - At(x + 1, y - 1)
                    - At(x - 1, y) - At(x + 1, y)
                    - At(x - 1, y + 1) - At(x, y + 1) - At(x + 1, y + 1);
                dst[y * w + x] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
            }
        }
        return dst;
    }
}
