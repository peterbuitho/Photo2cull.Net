using System.Threading.Channels;
using Photo2CullNet.Core.Classify;
using Photo2CullNet.Core.Imaging;

namespace Photo2CullNet.Core;

/// <summary>What sharpness-scoring mode to use for a photo.</summary>
public readonly record struct ScanMode
{
    public bool IsAuto { get; }
    public PhotoMode? Fixed { get; }

    private ScanMode(bool isAuto, PhotoMode? fixedMode)
    {
        IsAuto = isAuto;
        Fixed = fixedMode;
    }

    /// <summary>
    /// Classify each photo individually (face detection for Portrait,
    /// falling back to a sharpness-uniformity heuristic for Landscape vs
    /// Object).
    /// </summary>
    public static ScanMode Auto => new(true, null);

    /// <summary>
    /// Force this mode. Portrait still tries face detection (for a
    /// tighter, more accurate scoring region) and falls back to a
    /// centered crop if no face is found.
    /// </summary>
    public static ScanMode ForMode(PhotoMode mode) => new(false, mode);
}

public sealed record PhotoResult(
    string Path,
    PhotoMode Mode,
    double Score,
    Metrics Metrics,
    ulong PHash,
    int ThumbW,
    int ThumbH,
    byte[] ThumbRgb)
{
    public byte[] EncodeThumbPng() => SkiaImageCodec.EncodePng(new RgbImage(ThumbW, ThumbH, ThumbRgb));
}

public abstract record ScanEvent
{
    public sealed record Found(int Count) : ScanEvent;
    public sealed record Photo(PhotoResult Result) : ScanEvent;
    public sealed record Failed(string Path, string Error) : ScanEvent;
    public sealed record Done : ScanEvent;
}

public sealed record RecomputeResult(string Path, double Score, Metrics Metrics);

public abstract record RecomputeEvent
{
    public sealed record Result(RecomputeResult Value) : RecomputeEvent;
    public sealed record Done : RecomputeEvent;
}

/// <summary>Scan orchestration, ported from <c>scan.rs</c>.</summary>
public static class Scan
{
    /// <summary>
    /// Resolution cap for decoding: enough detail for a meaningful
    /// sharpness score without paying to decode full sensor resolution.
    /// </summary>
    private const int ScoreMaxDim = 1600;

    /// <summary>Cap for the preview thumbnail shown in the UI.</summary>
    private const int ThumbMaxDim = 220;

    /// <summary>
    /// Name of the subfolder (created inside the scanned root) that
    /// disqualified photos get moved into. Scans skip it entirely, so a
    /// photo moved there doesn't reappear (flagged all over again) on the
    /// next scan.
    /// </summary>
    public const string DisqualifiedDir = "disqualified";

    /// <summary>
    /// Classify (if Auto) and score a single already-decoded photo: the
    /// existing absolute sharpness score plus the full set of Overall-
    /// score factors.
    /// </summary>
    private static (PhotoMode Mode, double Score, Metrics Metrics) ClassifyAndScore(RgbImage img, ScanMode scanMode)
    {
        PhotoMode mode;
        FaceBox? face;
        if (scanMode.IsAuto)
        {
            var main = FaceDetector.DetectMainFace(img);
            if (main is { } f)
            {
                mode = PhotoMode.Portrait;
                face = f;
            }
            else
            {
                mode = Sharpness.GuessLandscapeOrObject(img);
                face = null;
            }
        }
        else
        {
            var m = scanMode.Fixed!.Value;
            face = m == PhotoMode.Portrait ? FaceDetector.DetectMainFace(img) : null;
            mode = m;
        }

        double score = Sharpness.Score(img, mode, face);
        var metrics = Metrics.Compute(img, score, face);
        return (mode, score, metrics);
    }

    /// <summary>
    /// Walks <paramref name="root"/> for supported photos (RAW or standard
    /// formats), skipping the disqualified subfolder, and scores each in
    /// parallel, streaming results as they complete. Call from a
    /// background task -- this blocks the calling thread pool worker for
    /// the whole scan.
    /// </summary>
    public static async Task RunScanAsync(
        string root, ScanMode scanMode, ChannelWriter<ScanEvent> writer, CancellationToken ct = default)
    {
        var files = WalkPhotos(root).ToList();
        await writer.WriteAsync(new ScanEvent.Found(files.Count), ct);

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        await Parallel.ForEachAsync(files, options, async (path, token) =>
        {
            try
            {
                var img = Photo.DecodePhoto(path, ScoreMaxDim);
                var (mode, score, metrics) = ClassifyAndScore(img, scanMode);
                var phash = Dedupe.DHash(img);

                var thumb = SkiaImageCodec.ResizeToFit(img, ThumbMaxDim);
                var result = new PhotoResult(path, mode, score, metrics, phash, thumb.Width, thumb.Height, thumb.Pixels);
                await writer.WriteAsync(new ScanEvent.Photo(result), token);
            }
            catch (Exception e)
            {
                await writer.WriteAsync(new ScanEvent.Failed(path, e.Message), token);
            }
        });

        // Not passed `ct`: callers rely on this to flip their "scanning"
        // flag back off even when the scan was cancelled mid-flight, so it
        // must go through regardless of the token's state by this point.
        await writer.WriteAsync(new ScanEvent.Done());
        writer.TryComplete();
    }

    /// <summary>
    /// Re-scores specific (path, forced mode) pairs -- used after the user
    /// manually overrides a photo's type -- without re-walking the folder
    /// or regenerating thumbnails (the crop used for those doesn't depend
    /// on mode).
    /// </summary>
    public static async Task RunRecomputeAsync(
        IReadOnlyList<(string Path, PhotoMode Mode)> items, ChannelWriter<RecomputeEvent> writer, CancellationToken ct = default)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        await Parallel.ForEachAsync(items, options, async (item, token) =>
        {
            try
            {
                var img = Photo.DecodePhoto(item.Path, ScoreMaxDim);
                var (_, score, metrics) = ClassifyAndScore(img, ScanMode.ForMode(item.Mode));
                await writer.WriteAsync(new RecomputeEvent.Result(new RecomputeResult(item.Path, score, metrics)), token);
            }
            catch
            {
                // Matches the Rust port: a recompute failure is silently
                // dropped rather than surfaced, since it only affects a
                // manually-overridden single photo.
            }
        });

        await writer.WriteAsync(new RecomputeEvent.Done());
        writer.TryComplete();
    }

    private static IEnumerable<string> WalkPhotos(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!string.Equals(Path.GetFileName(entry), DisqualifiedDir, StringComparison.Ordinal))
                    {
                        stack.Push(entry);
                    }
                }
                else if (Photo.IsSupportedPhoto(entry))
                {
                    yield return entry;
                }
            }
        }
    }
}
