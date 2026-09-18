namespace Photo2CullNet.Core.Imaging;

/// <summary>An 8-bit grayscale image, row-major, one byte per pixel.</summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public GrayImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height)
        {
            throw new ArgumentException(
                $"pixel buffer length {pixels.Length} does not match {width}x{height}");
        }
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public GrayImage(int width, int height) : this(width, height, new byte[width * height])
    {
    }

    public byte GetPixel(int x, int y) => Pixels[y * Width + x];

    public void SetPixel(int x, int y, byte v) => Pixels[y * Width + x] = v;
}
