using Photo2CullNet.Core.Classify;
using Photo2CullNet.Core.Imaging;

namespace Photo2CullNet.Core;

/// <summary>
/// The Overall-score ranking system: exposure, contrast, color, composition
/// and subject quality, plus the weighted blend that combines them with
/// sharpness into a single Overall score. Ported from <c>metrics.rs</c> --
/// see that file's doc comment for the reasoning behind each heuristic and
/// why composition/subject are deliberately approximate.
/// </summary>
public readonly record struct Metrics(
    double Sharpness,
    double Exposure,
    double Contrast,
    double Color,
    double? Composition,
    double? Subject)
{
    /// <summary>
    /// Compute every factor from an already-decoded photo, given its raw
    /// (absolute) sharpness score and, for Portraits, the detected main
    /// face.
    /// </summary>
    public static Metrics Compute(RgbImage rgb, double rawSharpness, FaceBox? face) => new(
        Sharpness: NormalizeSharpness(rawSharpness),
        Exposure: ExposureScore(rgb),
        Contrast: ContrastScore(rgb),
        Color: ColorScore(rgb),
        Composition: CompositionScore(rgb),
        Subject: face is { } f ? SubjectQualityScore(f, rgb.Width, rgb.Height) : null);

    /// <summary>
    /// Maps the unbounded raw variance-of-Laplacian score to 0-100 with a
    /// saturating (no hard ceiling) curve: raw=100 -&gt; 50, raw=300 -&gt;
    /// 75, raw=900 -&gt; 90.
    /// </summary>
    private static double NormalizeSharpness(double raw) => raw <= 0.0 ? 0.0 : 100.0 * raw / (raw + 100.0);

    /// <summary>
    /// 100 = midtone-centered with no clipping; penalized for both
    /// deviation from a midtone mean and for blown-highlight/crushed-
    /// shadow pixel fractions.
    /// </summary>
    private static double ExposureScore(RgbImage rgb)
    {
        var gray = ImageOps.ToGray(rgb);
        var pixels = gray.Pixels;
        double n = pixels.Length;
        if (n == 0.0) return 0.0;

        double mean = 0.0;
        int clippedCount = 0;
        foreach (var p in pixels)
        {
            mean += p;
            if (p <= 3 || p >= 252) clippedCount++;
        }
        mean /= n;
        double clipped = clippedCount / n;

        double brightnessScore = 100.0 * (1.0 - Math.Abs(mean - 128.0) / 128.0);
        double clippingPenalty = 100.0 * clipped;
        return Math.Clamp(brightnessScore - clippingPenalty, 0.0, 100.0);
    }

    /// <summary>
    /// 100 = strong tonal spread. Uses the luminance histogram's standard
    /// deviation, scaled so ~64 (a commonly-cited "good contrast" ballpark)
    /// maps to 100.
    /// </summary>
    private static double ContrastScore(RgbImage rgb)
    {
        var gray = ImageOps.ToGray(rgb);
        var pixels = gray.Pixels;
        double n = pixels.Length;
        if (n == 0.0) return 0.0;

        double mean = 0.0;
        foreach (var p in pixels) mean += p;
        mean /= n;

        double variance = 0.0;
        foreach (var p in pixels)
        {
            double d = p - mean;
            variance += d * d;
        }
        variance /= n;

        return Math.Clamp(100.0 * Math.Sqrt(variance) / 64.0, 0.0, 100.0);
    }

    /// <summary>
    /// 100 = highly colorful/vivid, via the Hasler-Süsstrunk colorfulness
    /// metric (a well-established, purely algorithmic measure -- not a
    /// judgment of whether the colors are "correct", just how vivid they
    /// are).
    /// </summary>
    private static double ColorScore(RgbImage rgb)
    {
        double n = (long)rgb.Width * rgb.Height;
        if (n == 0.0) return 0.0;

        double sumRg = 0, sumYb = 0, sumRg2 = 0, sumYb2 = 0;
        var px = rgb.Pixels;
        for (int i = 0; i < px.Length; i += 3)
        {
            double r = px[i], g = px[i + 1], b = px[i + 2];
            double rg = r - g;
            double yb = 0.5 * (r + g) - b;
            sumRg += rg;
            sumYb += yb;
            sumRg2 += rg * rg;
            sumYb2 += yb * yb;
        }

        double meanRg = sumRg / n;
        double meanYb = sumYb / n;
        double stdRg = Math.Sqrt(Math.Max(sumRg2 / n - meanRg * meanRg, 0.0));
        double stdYb = Math.Sqrt(Math.Max(sumYb2 / n - meanYb * meanYb, 0.0));

        double colorfulness = Math.Sqrt(stdRg * stdRg + stdYb * stdYb)
            + 0.3 * Math.Sqrt(meanRg * meanRg + meanYb * meanYb);

        // The original paper's "extremely colorful" bucket starts around 109-110.
        return Math.Clamp(100.0 * colorfulness / 110.0, 0.0, 100.0);
    }

    /// <summary>
    /// 100 = edge/detail energy concentrates near the four rule-of-thirds
    /// gridlines; 50 = no particular alignment; 0 = energy concentrates
    /// away from the gridlines. Uses the same Laplacian-magnitude map as
    /// sharpness as a crude "visual interest" proxy.
    /// </summary>
    private static double CompositionScore(RgbImage rgb)
    {
        var gray = ImageOps.ToGray(rgb);
        int w = gray.Width, h = gray.Height;
        if (w < 4 || h < 4) return 50.0;

        var lap = ImageOps.Laplacian(gray);
        if (lap.Length == 0) return 50.0;

        double bandW = Math.Max(w * 0.10, 1.0);
        double bandH = Math.Max(h * 0.10, 1.0);
        double[] thirdX = [w / 3.0, 2.0 * w / 3.0];
        double[] thirdY = [h / 3.0, 2.0 * h / 3.0];

        double total = 0.0, nearThirds = 0.0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double e = Math.Abs((double)lap[y * w + x]);
                total += e;
                bool nearX = thirdX.Any(tx => Math.Abs(x - tx) <= bandW);
                bool nearY = thirdY.Any(ty => Math.Abs(y - ty) <= bandH);
                if (nearX || nearY) nearThirds += e;
            }
        }
        if (total <= 0.0) return 50.0;

        double fraction = nearThirds / total;
        double bandFracX = Math.Min(2.0 * bandW / w, 1.0);
        double bandFracY = Math.Min(2.0 * bandH / h, 1.0);
        double baseline = 1.0 - (1.0 - bandFracX) * (1.0 - bandFracY);

        double lift = (fraction - baseline) / Math.Max(1.0 - baseline, 1e-6);
        return Math.Clamp(50.0 + 50.0 * lift, 0.0, 100.0);
    }

    /// <summary>
    /// Portrait-only: how prominent the detected face is in the frame, as
    /// a blend of size (bigger = more deliberate a subject, saturating
    /// around a fairly close 15%-of-frame crop) and centering (closer to
    /// the frame center scores higher).
    /// </summary>
    private static double SubjectQualityScore(FaceBox face, int imgW, int imgH)
    {
        if (imgW <= 0 || imgH <= 0) return 50.0;

        double areaFrac = (double)(face.Width * face.Height) / ((double)imgW * imgH);
        double sizeScore = Math.Clamp(100.0 * areaFrac / 0.15, 0.0, 100.0);

        double faceCx = (face.X1 + face.X2) / 2.0;
        double faceCy = (face.Y1 + face.Y2) / 2.0;
        double dx = faceCx - imgW / 2.0;
        double dy = faceCy - imgH / 2.0;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double maxDist = Math.Sqrt((double)imgW * imgW + (double)imgH * imgH) / 2.0;
        double centeringScore = Math.Clamp(100.0 * (1.0 - dist / Math.Max(maxDist, 1.0)), 0.0, 100.0);

        return 0.6 * sizeScore + 0.4 * centeringScore;
    }
}

/// <summary>
/// Blend weights for the six factors. Need not sum to 1.0 --
/// <see cref="Ranking.OverallScore"/> renormalizes over whatever factors
/// are actually available.
/// </summary>
public readonly record struct Weights(
    float Sharpness = 0.25f,
    float Exposure = 0.15f,
    float Contrast = 0.10f,
    float Color = 0.10f,
    float Composition = 0.25f,
    float Subject = 0.15f)
{
    public static readonly Weights Default = new();
}

public static class Ranking
{
    /// <summary>
    /// Weighted average of the available factors in <paramref name="m"/>,
    /// in the same 0-100 space as each individual factor.
    /// </summary>
    public static double OverallScore(Metrics m, Weights w)
    {
        double totalWeight = 0.0, weightedSum = 0.0;
        void Add(float weight, double value)
        {
            totalWeight += weight;
            weightedSum += weight * value;
        }

        Add(w.Sharpness, m.Sharpness);
        Add(w.Exposure, m.Exposure);
        Add(w.Contrast, m.Contrast);
        Add(w.Color, m.Color);
        if (m.Composition is { } c) Add(w.Composition, c);
        if (m.Subject is { } s) Add(w.Subject, s);

        return totalWeight <= 0.0 ? 0.0 : weightedSum / totalWeight;
    }
}
