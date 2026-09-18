using System.Numerics;
using Photo2CullNet.Core.Imaging;

namespace Photo2CullNet.Core;

/// <summary>
/// Phase 2 of the ranking system: detecting near-duplicate/burst-sequence
/// photos via perceptual hashing, and clustering them into groups. Ported
/// from <c>dedupe.rs</c>.
/// </summary>
public static class Dedupe
{
    /// <summary>
    /// 64-bit difference hash (dHash): resize to 9x8, grayscale, compare
    /// each pixel to its right neighbor. Near-identical images --
    /// duplicates, consecutive burst frames -- produce hashes with a small
    /// Hamming distance; visually different images diverge quickly.
    /// </summary>
    public static ulong DHash(RgbImage rgb)
    {
        var small = SkiaImageCodec.ResizeExactTo(rgb, 9, 8);
        var gray = ImageOps.ToGray(small);

        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                byte left = gray.GetPixel(x, y);
                byte right = gray.GetPixel(x + 1, y);
                if (left > right)
                {
                    hash |= 1UL << bit;
                }
                bit++;
            }
        }
        return hash;
    }

    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>
    /// Clusters (path, dHash) pairs whose members are all pairwise
    /// reachable within <paramref name="maxDistance"/> Hamming distance of
    /// each other (via union-find, not just distance-to-the-first-member --
    /// so a chain of gradually-drifting near-duplicates still ends up in
    /// one group). Only clusters with 2+ members are returned, largest
    /// first.
    ///
    /// O(n^2) hash comparisons, but each is a single XOR + popcount, so
    /// this stays well under a second even for several thousand photos --
    /// fine to run synchronously from a button click.
    /// </summary>
    public static List<List<string>> GroupDuplicates(IReadOnlyList<(string Path, ulong Hash)> photos, int maxDistance)
    {
        int n = photos.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (HammingDistance(photos[i].Hash, photos[j].Hash) <= maxDistance)
                {
                    int ri = Find(i), rj = Find(j);
                    if (ri != rj) parent[ri] = rj;
                }
            }
        }

        var groups = new Dictionary<int, List<string>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                groups[root] = list = [];
            }
            list.Add(photos[i].Path);
        }

        return groups.Values.Where(g => g.Count > 1).OrderByDescending(g => g.Count).ToList();
    }
}
