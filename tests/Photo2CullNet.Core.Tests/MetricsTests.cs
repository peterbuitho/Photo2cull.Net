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
}
