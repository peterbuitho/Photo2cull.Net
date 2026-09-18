namespace Photo2CullNet.Core.Tests;

public class MetricsTests
{
    [Fact]
    public void OverallScore_WithNoComposedFactors_RenormalizesOverPresentOnes()
    {
        var metrics = new Metrics(Sharpness: 100, Exposure: 0, Contrast: 0, Color: 0, Composition: null, Subject: null);
        var weights = new Weights(Sharpness: 1f, Exposure: 1f, Contrast: 0f, Color: 0f, Composition: 1f, Subject: 1f);

        // Composition/Subject are absent, so only Sharpness/Exposure/Contrast/Color (weights 1,1,0,0) count.
        double score = Ranking.OverallScore(metrics, weights);

        Assert.Equal(50.0, score, precision: 6);
    }

    [Fact]
    public void OverallScore_WithZeroTotalWeight_IsZero()
    {
        var metrics = new Metrics(Sharpness: 100, Exposure: 100, Contrast: 100, Color: 100, Composition: 100, Subject: 100);
        var weights = new Weights(0, 0, 0, 0, 0, 0);

        Assert.Equal(0.0, Ranking.OverallScore(metrics, weights));
    }

    /// <summary>
    /// Regression guard: for a struct, `new Weights()` invokes the
    /// implicit parameterless constructor (all fields zeroed), NOT the
    /// primary constructor's declared defaults, even though it looks like
    /// it should. `Weights.Default` must spell its values out explicitly.
    /// </summary>
    [Fact]
    public void Default_IsNotAllZero()
    {
        var w = Weights.Default;

        Assert.Equal(0.25f, w.Sharpness);
        Assert.Equal(0.15f, w.Exposure);
        Assert.Equal(0.10f, w.Contrast);
        Assert.Equal(0.10f, w.Color);
        Assert.Equal(0.25f, w.Composition);
        Assert.Equal(0.15f, w.Subject);
    }
}
