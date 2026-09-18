namespace Photo2CullNet.Core.Imaging;

/// <summary>
/// A decoded RGB8 image: width * height pixels, 3 bytes each (R, G, B),
/// row-major. Mirrors the shape of Rust's <c>image::RgbImage</c> so the
/// scoring/dedupe code ported from it stays a close read-along.
/// </summary>
public sealed class RgbImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Packed R,G,B,R,G,B,... row-major pixel data.</summary>
    public byte[] Pixels { get; }

    public RgbImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 3)
        {
            throw new ArgumentException(
                $"pixel buffer length {pixels.Length} does not match {width}x{height}x3");
        }
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public RgbImage(int width, int height) : this(width, height, new byte[width * height * 3])
    {
    }

    public (byte R, byte G, byte B) GetPixel(int x, int y)
    {
        int i = (y * Width + x) * 3;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2]);
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b)
    {
        int i = (y * Width + x) * 3;
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
    }
}
