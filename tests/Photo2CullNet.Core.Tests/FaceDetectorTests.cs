using Photo2CullNet.Core.Classify;

namespace Photo2CullNet.Core.Tests;

public class FaceDetectorTests
{
    /// <summary>
    /// Guards the prior-box layout against silent drift: it must line up
    /// 1:1 with the model's actual output row count (4420, confirmed by
    /// inspecting the loaded model's output shape), or box decoding
    /// silently misaligns priors with the wrong regression deltas.
    /// </summary>
    [Fact]
    public void PriorCount_MatchesModelOutput()
    {
        Assert.Equal(4420, FaceDetector.PriorCount);
    }
}
