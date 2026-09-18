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
}
