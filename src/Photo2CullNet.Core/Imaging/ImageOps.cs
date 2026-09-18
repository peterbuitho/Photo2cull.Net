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
    /// 3x3 Laplacian edge filter, matching <c>imageproc::kernel::LAPLACIAN_3X3</c>
    /// exactly -- verified against imageproc 0.27.0's actual source (not
    /// memory/assumption): kernel is the 4-connected
    /// [[0,1,0],[1,-4,1],[0,1,0]], not the 8-connected [[-1]*8 around a
    /// +8 center] this port originally (wrongly) used. That earlier
    /// kernel's response magnitude runs ~2x higher for the same input,
    /// which inflated variance-of-Laplacian scores roughly 4x -- enough
    /// to blow raw scores calibrated in the Rust app's 24-330 range out
    /// past 10000. Border pixels are handled by clamping (replicating the
    /// edge), matching imageproc's <c>filter()</c> exactly (confirmed from
    /// its source: `min(height-1, max(0, y + k_y - k_height/2))`).
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
                int sum = At(x, y - 1) + At(x - 1, y) + At(x + 1, y) + At(x, y + 1) - 4 * At(x, y);
                dst[y * w + x] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
            }
        }
        return dst;
    }

    /// <summary>
    /// General resize matching the <c>image</c> crate's <c>Triangle</c>
    /// filter (what every resize in the Rust app uses): a bilinear/tent
    /// kernel whose support widens proportionally to the downscale ratio.
    /// That widening matters a lot here -- a plain small-footprint
    /// bilinear resize (e.g. a naive GPU-style resize) does NOT properly
    /// area-average when shrinking a large image a lot, so it aliases:
    /// high-frequency noise survives the resize instead of being averaged
    /// away, which inflates a high-frequency-sensitive metric like
    /// variance-of-Laplacian sharpness by several times. Implemented as
    /// two separable 1D passes (horizontal then vertical).
    /// </summary>
    public static RgbImage ResizeTriangle(RgbImage src, int dstW, int dstH)
    {
        var afterHorizontal = dstW == src.Width ? src : ResizeAxis(src, dstW, horizontal: true);
        return dstH == afterHorizontal.Height ? afterHorizontal : ResizeAxis(afterHorizontal, dstH, horizontal: false);
    }

    /// <summary>Triangle filter's own support radius (image-rs: `Triangle { support: 1.0 }`).</summary>
    private const double TriangleSupport = 1.0;

    private static RgbImage ResizeAxis(RgbImage src, int dstLen, bool horizontal)
    {
        int srcLen = horizontal ? src.Width : src.Height;
        int otherLen = horizontal ? src.Height : src.Width;
        var dst = horizontal ? new RgbImage(dstLen, otherLen) : new RgbImage(otherLen, dstLen);

        double scale = (double)srcLen / dstLen;
        double filterScale = Math.Max(scale, 1.0); // widen support only when downscaling
        double support = TriangleSupport * filterScale;

        // The window width is bounded by the (constant, for this axis
        // pass) support radius, regardless of which destination index
        // `i` we're at -- allocate once up front rather than once per
        // `i` (or risk a stack overflow allocating per-iteration).
        var weights = new double[(int)Math.Ceiling(2 * support) + 2];

        // Indexes the underlying byte arrays directly rather than going
        // through GetPixel/SetPixel's tuple-returning calls -- this loop
        // runs many times per photo (once per output pixel per source
        // pixel within the filter's support), so the method-call/tuple
        // overhead is worth avoiding.
        var srcPixels = src.Pixels;
        var dstPixels = dst.Pixels;
        int srcW = src.Width, dstW = dst.Width;

        for (int i = 0; i < dstLen; i++)
        {
            double center = (i + 0.5) * scale;
            int left = Math.Max((int)Math.Floor(center - support), 0);
            int right = Math.Min((int)Math.Ceiling(center + support), srcLen - 1);

            // Weights depend only on the destination index `i`, not the
            // row/column being resampled, so compute them once per `i`
            // and reuse across every row (or column) of the other axis.
            double weightSum = 0;
            for (int j = left; j <= right; j++)
            {
                double x = (j + 0.5 - center) / filterScale;
                double w = Math.Max(0.0, 1.0 - Math.Abs(x));
                weights[j - left] = w;
                weightSum += w;
            }
            if (weightSum <= 0)
            {
                left = right = Math.Clamp((int)center, 0, srcLen - 1);
                weights[0] = weightSum = 1.0;
            }
            double invSum = 1.0 / weightSum;

            for (int o = 0; o < otherLen; o++)
            {
                double r = 0, g = 0, b = 0;
                for (int j = left; j <= right; j++)
                {
                    int idx = horizontal ? (o * srcW + j) * 3 : (j * srcW + o) * 3;
                    double w = weights[j - left];
                    r += srcPixels[idx] * w;
                    g += srcPixels[idx + 1] * w;
                    b += srcPixels[idx + 2] * w;
                }

                int dstIdx = horizontal ? (o * dstW + i) * 3 : (i * dstW + o) * 3;
                dstPixels[dstIdx] = (byte)Math.Clamp(Math.Round(r * invSum), 0, 255);
                dstPixels[dstIdx + 1] = (byte)Math.Clamp(Math.Round(g * invSum), 0, 255);
                dstPixels[dstIdx + 2] = (byte)Math.Clamp(Math.Round(b * invSum), 0, 255);
            }
        }
        return dst;
    }
}
