using Photo2CullNet.Core;

namespace Photo2CullNet.Core.Tests;

public class DedupeTests
{
    [Fact]
    public void HammingDistance_CountsDifferingBits()
    {
        Assert.Equal(0, Dedupe.HammingDistance(0b0000, 0b0000));
        Assert.Equal(4, Dedupe.HammingDistance(0b0000, 0b1111));
        Assert.Equal(64, Dedupe.HammingDistance(ulong.MaxValue, 0));
    }

    [Fact]
    public void GroupDuplicates_GroupsCloseHashesAndExcludesSingletons()
    {
        var photos = new List<(string Path, ulong Hash)>
        {
            ("a", 0b0000_0000UL),
            ("b", 0b0000_0001UL), // distance 1 from a
            ("c", 0b1111_1111UL), // distance 8 from a -- excluded
        };

        var groups = Dedupe.GroupDuplicates(photos, maxDistance: 2);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void GroupDuplicates_ChainsTransitivelyThroughUnionFind()
    {
        // a-b close, b-c close, but a-c alone would exceed maxDistance --
        // should still all land in one group via the b bridge.
        var photos = new List<(string Path, ulong Hash)>
        {
            ("a", 0b0000_0000UL),
            ("b", 0b0000_0011UL), // distance 2 from a
            ("c", 0b0000_1111UL), // distance 2 from b, distance 4 from a
        };

        var groups = Dedupe.GroupDuplicates(photos, maxDistance: 2);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
    }
}
