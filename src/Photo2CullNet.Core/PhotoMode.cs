namespace Photo2CullNet.Core;

public enum PhotoMode
{
    Landscape,
    Portrait,
    Object,
}

public static class PhotoModeExtensions
{
    public static readonly PhotoMode[] All = [PhotoMode.Landscape, PhotoMode.Portrait, PhotoMode.Object];

    public static string Label(this PhotoMode mode) => mode switch
    {
        PhotoMode.Landscape => "Landscape",
        PhotoMode.Portrait => "Portrait",
        PhotoMode.Object => "Object",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>
    /// Fraction of width/height kept (centered) when scoring; 1.0 = whole frame.
    ///
    /// Used when there's no detected face to score instead (see
    /// <c>Sharpness.Score</c>): landscapes are scored edge-to-edge, while a
    /// centered/tighter crop stands in for "where the subject probably is"
    /// for portraits/objects, so a deliberately blurred background doesn't
    /// tank the score.
    /// </summary>
    public static float RegionFraction(this PhotoMode mode) => mode switch
    {
        PhotoMode.Landscape => 1.0f,
        PhotoMode.Object => 0.6f,
        PhotoMode.Portrait => 0.45f,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
