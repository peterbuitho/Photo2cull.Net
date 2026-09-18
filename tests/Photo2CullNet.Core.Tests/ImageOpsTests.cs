using Photo2CullNet.Core.Imaging;

namespace Photo2CullNet.Core.Tests;

public class ImageOpsTests
{
    [Fact]
    public void Laplacian_OfFlatImage_IsZeroEverywhere()
    {
        var flat = new GrayImage(10, 10);
        Array.Fill(flat.Pixels, (byte)128);

        var lap = ImageOps.Laplacian(flat);

        Assert.All(lap, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Laplacian_OfSingleBrightPixel_IsNonZeroAtItsNeighbors()
    {
        var img = new GrayImage(5, 5);
        img.SetPixel(2, 2, 255);

        var lap = ImageOps.Laplacian(img);

        Assert.NotEqual(0, lap[2 * 5 + 2]);
    }

    [Fact]
    public void CenterCrop_WithFullFraction_ReturnsSameImage()
    {
        var img = new GrayImage(8, 6);
        var cropped = ImageOps.CenterCrop(img, 1.0f);
        Assert.Same(img, cropped);
    }

    [Fact]
    public void CenterCrop_HalvesEachDimension()
    {
        var img = new GrayImage(10, 20);
        var cropped = ImageOps.CenterCrop(img, 0.5f);
        Assert.Equal(5, cropped.Width);
        Assert.Equal(10, cropped.Height);
    }

    [Fact]
    public void ToGray_OfWhiteImage_IsWhite()
    {
        var rgb = new RgbImage(2, 2);
        Array.Fill(rgb.Pixels, (byte)255);

        var gray = ImageOps.ToGray(rgb);

        Assert.All(gray.Pixels, v => Assert.Equal(255, v));
    }

    /// <summary>
    /// Regression guard: pins the exact kernel values, not just "some
    /// nonzero response". An earlier version of this port used the wrong
    /// kernel entirely (8-connected, center weight +8) instead of
    /// imageproc's actual <c>LAPLACIAN_3X3</c> (4-connected,
    /// [[0,1,0],[1,-4,1],[0,1,0]]) -- close enough to "look right" (still
    /// zero on flat regions, still nonzero near edges) that the earlier
    /// tests above didn't catch it, but it inflated variance-of-Laplacian
    /// scores by roughly 4x against the Rust app's calibration.
    /// </summary>
    [Fact]
    public void Laplacian_MatchesImageprocs4ConnectedKernelExactly()
    {
        // A single bright pixel at (1,1) in an otherwise-black 3x3 image.
        var img = new GrayImage(3, 3);
        img.SetPixel(1, 1, 255);

        var lap = ImageOps.Laplacian(img);

        // At the bright center: 4*center - (N+W+E+S) = -(4*255) = -1020,
        // clamped into i16 range (it already fits, but matches imageproc's
        // own S::clamp step regardless).
        Assert.Equal(-1020, lap[1 * 3 + 1]);

        // At each of its 4 orthogonal neighbors (e.g. north, at (1,0)):
        // that pixel's own value is 0, and exactly one of its own 4
        // neighbors (south, back to the bright pixel) is 255 -- the rest,
        // including the border-clamped one, are 0.
        Assert.Equal(255, lap[0 * 3 + 1]); // north of the bright pixel
        Assert.Equal(255, lap[1 * 3 + 0]); // west
        Assert.Equal(255, lap[1 * 3 + 2]); // east
        Assert.Equal(255, lap[2 * 3 + 1]); // south

        // The four corners never neighbor the bright pixel under a
        // 4-connected kernel (only under the wrong 8-connected one).
        Assert.Equal(0, lap[0 * 3 + 0]);
        Assert.Equal(0, lap[0 * 3 + 2]);
        Assert.Equal(0, lap[2 * 3 + 0]);
        Assert.Equal(0, lap[2 * 3 + 2]);
    }

    [Fact]
    public void ResizeTriangle_OfUniformImage_StaysUniform()
    {
        var img = new RgbImage(40, 30);
        for (int i = 0; i < img.Pixels.Length; i += 3) { img.Pixels[i] = 10; img.Pixels[i + 1] = 20; img.Pixels[i + 2] = 30; }

        var resized = ImageOps.ResizeTriangle(img, 7, 5);

        Assert.Equal(7, resized.Width);
        Assert.Equal(5, resized.Height);
        for (int y = 0; y < resized.Height; y++)
        {
            for (int x = 0; x < resized.Width; x++)
            {
                var (r, g, b) = resized.GetPixel(x, y);
                Assert.Equal((10, 20, 30), (r, g, b));
            }
        }
    }

    /// <summary>
    /// Regression guard for the aliasing bug: downscaling a large,
    /// alternating-pixel (maximally high-frequency) image with a properly
    /// area-averaging filter must wash the alternation out close to the
    /// midpoint gray -- a small-footprint bilinear resize (what this port
    /// used before switching to a variable-support Triangle filter)
    /// aliases instead, letting most of that high-frequency content
    /// survive into the downscaled image.
    /// </summary>
    [Fact]
    public void ResizeTriangle_LargeDownscale_AveragesAwayHighFrequencyContent()
    {
        var img = new GrayImage(400, 300);
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                img.SetPixel(x, y, (byte)((x + y) % 2 == 0 ? 0 : 255));
            }
        }
        var rgb = new RgbImage(img.Width, img.Height);
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            rgb.Pixels[i * 3] = rgb.Pixels[i * 3 + 1] = rgb.Pixels[i * 3 + 2] = img.Pixels[i];
        }

        var resized = ImageOps.ResizeTriangle(rgb, 40, 30);

        foreach (var (r, _, _) in Enumerable.Range(0, resized.Width * resized.Height)
            .Select(i => resized.GetPixel(i % resized.Width, i / resized.Width)))
        {
            Assert.InRange(r, 100, 155); // averaged toward ~127.5, not still near 0 or 255
        }
    }
}
