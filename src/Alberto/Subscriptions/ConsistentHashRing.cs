namespace Alberto.Subscriptions;

internal static class ConsistentHashRing
{
    private const int VirtualNodesPerNode = 150;

    public sealed record VirtualNode(uint Hash, string NodeId);

    public static IReadOnlyList<VirtualNode> Build(IReadOnlyList<string> nodeIds)
    {
        var ring = new List<VirtualNode>();
        foreach (var nodeId in nodeIds)
        {
            for (var i = 0; i < VirtualNodesPerNode; i++)
            {
                var hash = HashToUInt32($"{nodeId}:{i}");
                ring.Add(new VirtualNode(hash, nodeId));
            }
        }
        ring.Sort((a, b) => a.Hash.CompareTo(b.Hash));
        return ring;
    }

    public static string GetNodeForTenant(IReadOnlyList<VirtualNode> ring, string tenantId)
    {
        if (ring.Count == 0) throw new InvalidOperationException("Ring is empty.");

        var hash = HashToUInt32(tenantId);

        var lo = 0;
        var hi = ring.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (ring[mid].Hash < hash) lo = mid + 1;
            else hi = mid;
        }

        var idx = lo >= ring.Count ? 0 : lo;
        return ring[idx].NodeId;
    }

    private static uint HashToUInt32(string input)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}
