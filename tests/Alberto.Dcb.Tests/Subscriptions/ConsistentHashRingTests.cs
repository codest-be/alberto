using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

public class ConsistentHashRingTests
{
    [Fact]
    public void Build_SingleNode_AllTenantsAssignedToIt()
    {
        var nodeIds = new[] { "node-1" };
        var ring = ConsistentHashRing.Build(nodeIds);

        for (var i = 0; i < 100; i++)
        {
            var assigned = ConsistentHashRing.GetNodeForTenant(ring, $"tenant-{i}");
            Assert.Equal("node-1", assigned);
        }
    }

    [Fact]
    public void Build_TwoNodes_RoughlyEvenDistribution()
    {
        var nodeIds = new[] { "node-1", "node-2" };
        var ring = ConsistentHashRing.Build(nodeIds);

        var counts = new Dictionary<string, int> { ["node-1"] = 0, ["node-2"] = 0 };
        for (var i = 0; i < 100; i++)
        {
            var assigned = ConsistentHashRing.GetNodeForTenant(ring, $"tenant-{i}");
            counts[assigned]++;
        }

        // Each node should receive between 40 and 60 tenants
        Assert.InRange(counts["node-1"], 40, 60);
        Assert.InRange(counts["node-2"], 40, 60);
    }

    [Fact]
    public void Build_ThreeNodes_EvenDistribution()
    {
        var nodeIds = new[] { "node-1", "node-2", "node-3" };
        var ring = ConsistentHashRing.Build(nodeIds);

        var counts = new Dictionary<string, int> { ["node-1"] = 0, ["node-2"] = 0, ["node-3"] = 0 };
        for (var i = 0; i < 100; i++)
        {
            var assigned = ConsistentHashRing.GetNodeForTenant(ring, $"tenant-{i}");
            counts[assigned]++;
        }

        // Each node should receive no more than 43 tenants (within 10% of 1/3)
        Assert.InRange(counts["node-1"], 1, 43);
        Assert.InRange(counts["node-2"], 1, 43);
        Assert.InRange(counts["node-3"], 1, 43);
    }

    [Fact]
    public void GetNodeForTenant_SameTenantSameResult()
    {
        var nodeIds = new[] { "node-1", "node-2", "node-3" };
        var ring = ConsistentHashRing.Build(nodeIds);

        for (var i = 0; i < 50; i++)
        {
            var tenantId = $"tenant-{i}";
            var first = ConsistentHashRing.GetNodeForTenant(ring, tenantId);
            var second = ConsistentHashRing.GetNodeForTenant(ring, tenantId);
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void AddNode_MovesMinimalTenants()
    {
        const int tenantCount = 100;
        var twoNodes = new[] { "node-1", "node-2" };
        var threeNodes = new[] { "node-1", "node-2", "node-3" };

        var ringTwo = ConsistentHashRing.Build(twoNodes);
        var ringThree = ConsistentHashRing.Build(threeNodes);

        var movedCount = 0;
        for (var i = 0; i < tenantCount; i++)
        {
            var tenantId = $"tenant-{i}";
            var before = ConsistentHashRing.GetNodeForTenant(ringTwo, tenantId);
            var after = ConsistentHashRing.GetNodeForTenant(ringThree, tenantId);
            if (before != after) movedCount++;
        }

        // Adding a 3rd node to a 2-node ring should move at most 50 tenants
        Assert.True(movedCount <= 50,
            $"Expected at most 50 tenants to move, but {movedCount} moved.");
    }
}
