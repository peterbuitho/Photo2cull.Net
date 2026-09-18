using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Photo2CullNet.Core.Imaging;

namespace Photo2CullNet.Core.Classify;

/// <summary>
/// Per-photo subject classification: face detection (for Portrait) via a
/// bundled ONNX model. Ported from <c>classify.rs</c>.
///
/// Model: "RFB-320" from Ultra-Light-Fast-Generic-Face-Detector-1MB (MIT
/// licensed, see Assets/Models/NOTICE.md). Its exported ONNX graph outputs
/// raw, un-decoded SSD regression deltas relative to a fixed set of prior
/// (anchor) boxes; prior generation and box decoding are reimplemented here
/// from the original repo's box_utils.py / fd_config.py, matching the Rust
/// port exactly.
/// </summary>
public static class FaceDetector
{
    private const int InputW = 320;
    private const int InputH = 240;
    private const float ConfThreshold = 0.7f;
    private const float IouThreshold = 0.3f;
    private const float CenterVariance = 0.1f;
    private const float SizeVariance = 0.2f;

    /// <summary>
    /// Ignore detections smaller than this fraction of the frame area: a
    /// tiny face in the background shouldn't turn an otherwise-landscape
    /// shot into a "Portrait". Kept deliberately low -- see classify.rs's
    /// comment for the real-photo verification behind this number.
    /// </summary>
    private const float MinFaceAreaFraction = 0.001f;

    private const string ModelResourceName = "Photo2CullNet.Core.Assets.Models.version-RFB-320.onnx";

    private static readonly Lazy<InferenceSession> Model = new(LoadModel);
    private static readonly Lazy<float[][]> Priors = new(GeneratePriors);

    private static InferenceSession LoadModel()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ModelResourceName)
            ?? throw new InvalidOperationException($"embedded model resource not found: {ModelResourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return new InferenceSession(ms.ToArray());
    }

    /// <summary>
    /// (center_x, center_y, w, h), normalized 0..1, matching the model's
    /// prior (anchor) box layout for its 320x240 input: 4 feature-map
    /// layers, each with its own grid size and set of anchor box sizes (in
    /// pixels).
    /// </summary>
    private static float[][] GeneratePriors()
    {
        (int Fw, int Fh, float[] MinBoxes)[] layers =
        [
            (40, 30, [10.0f, 16.0f, 24.0f]),
            (20, 15, [32.0f, 48.0f]),
            (10, 8, [64.0f, 96.0f]),
            (5, 4, [128.0f, 192.0f, 256.0f]),
        ];

        var priors = new List<float[]>();
        foreach (var (fw, fh, minBoxes) in layers)
        {
            for (int j = 0; j < fh; j++)
            {
                for (int i = 0; i < fw; i++)
                {
                    float xCenter = (i + 0.5f) / fw;
                    float yCenter = (j + 0.5f) / fh;
                    foreach (var minBox in minBoxes)
                    {
                        priors.Add([
                            xCenter,
                            yCenter,
                            Math.Clamp(minBox / InputW, 0.0f, 1.0f),
                            Math.Clamp(minBox / InputH, 0.0f, 1.0f),
                        ]);
                    }
                }
            }
        }
        return priors.ToArray();
    }

    private static float Iou(float[] a, float[] b)
    {
        float ix1 = Math.Max(a[0], b[0]);
        float iy1 = Math.Max(a[1], b[1]);
        float ix2 = Math.Min(a[2], b[2]);
        float iy2 = Math.Min(a[3], b[3]);
        float inter = Math.Max(ix2 - ix1, 0f) * Math.Max(iy2 - iy1, 0f);
        float areaA = Math.Max(a[2] - a[0], 0f) * Math.Max(a[3] - a[1], 0f);
        float areaB = Math.Max(b[2] - b[0], 0f) * Math.Max(b[3] - b[1], 0f);
        return inter / (areaA + areaB - inter + 1e-5f);
    }

    private static List<(float Score, float[] Box)> Nms(List<(float Score, float[] Box)> candidates)
    {
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<(float Score, float[] Box)>();
        foreach (var (score, box) in candidates)
        {
            if (kept.All(k => Iou(k.Box, box) <= IouThreshold))
            {
                kept.Add((score, box));
            }
        }
        return kept;
    }

    /// <summary>
    /// Detect faces in <paramref name="rgb"/>, returned in its own pixel
    /// coordinates, most confident first, with overlapping detections
    /// suppressed. Returns an empty list (rather than throwing) on any
    /// inference failure -- classifying a photo as non-portrait is a
    /// reasonable degradation, not worth failing the whole scan over.
    /// </summary>
    public static List<FaceBox> DetectFaces(RgbImage rgb)
    {
        if (rgb.Width == 0 || rgb.Height == 0)
        {
            return [];
        }

        try
        {
            var resized = SkiaImageCodec.ResizeExactTo(rgb, InputW, InputH);
            var input = new DenseTensor<float>([1, 3, InputH, InputW]);
            for (int c = 0; c < 3; c++)
            {
                for (int y = 0; y < InputH; y++)
                {
                    for (int x = 0; x < InputW; x++)
                    {
                        var (r, g, b) = resized.GetPixel(x, y);
                        byte channel = c == 0 ? r : c == 1 ? g : b;
                        input[0, c, y, x] = (channel - 127f) / 128f;
                    }
                }
            }

            var session = Model.Value;
            string inputName = session.InputMetadata.Keys.First();
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
            var resultList = results.ToList();
            var confidences = resultList[0].AsEnumerable<float>().ToArray();
            var boxes = resultList[1].AsEnumerable<float>().ToArray();

            var priors = Priors.Value;
            int n = Math.Min(confidences.Length / 2, priors.Length);
            var candidates = new List<(float Score, float[] Box)>();
            for (int i = 0; i < n; i++)
            {
                float faceScore = confidences[i * 2 + 1];
                if (faceScore <= ConfThreshold) continue;

                var (pcx, pcy, pw, ph) = (priors[i][0], priors[i][1], priors[i][2], priors[i][3]);
                float lx = boxes[i * 4], ly = boxes[i * 4 + 1], lw = boxes[i * 4 + 2], lh = boxes[i * 4 + 3];

                float cx = lx * CenterVariance * pw + pcx;
                float cy = ly * CenterVariance * ph + pcy;
                float w = MathF.Exp(lw * SizeVariance) * pw;
                float h = MathF.Exp(lh * SizeVariance) * ph;

                candidates.Add((faceScore, [cx - w / 2f, cy - h / 2f, cx + w / 2f, cy + h / 2f]));
            }

            float frameArea = rgb.Width * (float)rgb.Height;
            return Nms(candidates)
                .Select(c =>
                {
                    var box = c.Box;
                    return new FaceBox(
                        Math.Clamp(box[0], 0f, 1f) * rgb.Width,
                        Math.Clamp(box[1], 0f, 1f) * rgb.Height,
                        Math.Clamp(box[2], 0f, 1f) * rgb.Width,
                        Math.Clamp(box[3], 0f, 1f) * rgb.Height,
                        c.Score);
                })
                .Where(f => f.Area / frameArea >= MinFaceAreaFraction)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// "The" subject face, if any -- the largest detection by area, on the
    /// assumption that the main subject of a portrait is usually the most
    /// prominent face in frame.
    /// </summary>
    public static FaceBox? DetectMainFace(RgbImage rgb)
    {
        var faces = DetectFaces(rgb);
        return faces.Count == 0 ? null : faces.OrderByDescending(f => f.Area).First();
    }

    /// <summary>Exposed for the prior-count regression test.</summary>
    internal static int PriorCount => Priors.Value.Length;
}
