using Photo2CullNet.Core.Classify;
using Photo2CullNet.Core.Imaging;

namespace Photo2CullNet.Core;

/// <summary>Variance-of-Laplacian sharpness scoring, ported from <c>sharpness.rs</c>.</summary>
public static class Sharpness
{
    /// <summary>
    /// Crop around a detected face, padded out to ~1.7x its size (centered
    /// on the face) so the sample includes a bit of surrounding context
    /// rather than just the tight face rectangle, clamped to the image
    /// bounds.
    /// </summary>
    public static GrayImage FaceCrop(GrayImage img, FaceBox face)
    {
        float cx = (face.X1 + face.X2) / 2f;
        float cy = (face.Y1 + face.Y2) / 2f;
        float w = Math.Max((face.X2 - face.X1) * 1.7f, 1f);
        float h = Math.Max((face.Y2 - face.Y1) * 1.7f, 1f);

        float x = Math.Clamp(cx - w / 2f, 0f, img.Width - 1f);
        float y = Math.Clamp(cy - h / 2f, 0f, img.Height - 1f);
        int cw = Math.Max((int)Math.Min(w, img.Width - x), 1);
        int ch = Math.Max((int)Math.Min(h, img.Height - y), 1);

        return ImageOps.Crop(img, (int)x, (int)y, cw, ch);
    }

    private static double VarianceOfLaplacian(GrayImage region)
    {
        var lap = ImageOps.Laplacian(region);
        if (lap.Length == 0)
        {
            return 0.0;
        }

        double n = lap.Length;
        double mean = 0.0;
        foreach (var v in lap)
        {
            mean += v;
        }
        mean /= n;

        double sumSq = 0.0;
        foreach (var v in lap)
        {
            double d = v - mean;
            sumSq += d * d;
        }
        return sumSq / n;
    }

    /// <summary>
    /// Variance-of-Laplacian sharpness score. Higher means sharper. If
    /// <paramref name="face"/> is given (a Portrait with a detected face),
    /// scores that face region; otherwise falls back to a centered crop
    /// sized per <paramref name="mode"/>.
    /// </summary>
    public static double Score(RgbImage rgb, PhotoMode mode, FaceBox? face)
    {
        var gray = ImageOps.ToGray(rgb);
        var region = face is { } f ? FaceCrop(gray, f) : ImageOps.CenterCrop(gray, mode.RegionFraction());
        return VarianceOfLaplacian(region);
    }

    /// <summary>
    /// When no face is found, guess Landscape vs Object from how uniform
    /// the sharpness is across the frame. A single object shot with
    /// shallow depth of field is much sharper in the center than at the
    /// edges; a landscape (typically a small aperture, front-to-back
    /// focus) is comparatively uniform. This is a coarse heuristic, not
    /// real scene classification.
    /// </summary>
    public static PhotoMode GuessLandscapeOrObject(RgbImage rgb)
    {
        var gray = ImageOps.ToGray(rgb);
        double full = VarianceOfLaplacian(gray);
        if (full <= 1.0)
        {
            return PhotoMode.Landscape;
        }

        double center = VarianceOfLaplacian(ImageOps.CenterCrop(gray, 0.5f));
        return center / full > 1.6 ? PhotoMode.Object : PhotoMode.Landscape;
    }
}
