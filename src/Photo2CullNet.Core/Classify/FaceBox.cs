namespace Photo2CullNet.Core.Classify;

/// <summary>
/// A detected face, in pixel coordinates of the image it was detected in.
/// </summary>
public readonly record struct FaceBox(float X1, float Y1, float X2, float Y2, float Score)
{
    public float Width => Math.Max(X2 - X1, 0f);
    public float Height => Math.Max(Y2 - Y1, 0f);
    public float Area => Width * Height;
}
