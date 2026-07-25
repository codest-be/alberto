using System.Diagnostics;
using Alberto.Dcb.Tenancy;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Builds the tag set every consume metric carries.
/// </summary>
/// <remarks>
/// A sharded module's processors run under the physical key <c>module#shard</c>, which is an
/// implementation detail nobody wants to see in a dashboard — and worse, one that would split a
/// module's throughput into series that no query can add back up. The module key and the shard id
/// are therefore reported as separate tags: sum over <c>shard</c> for the module's total, group by
/// it to see one database lagging.
/// </remarks>
internal static class TelemetryTags
{
    internal static TagList ForModule(string processorId, string moduleKey)
    {
        var tags = new TagList
        {
            { "processor", processorId },
            { "module", ShardKey.ModuleOf(moduleKey) },
        };

        if (ShardKey.ShardOf(moduleKey) is { } shardId)
            tags.Add("shard", shardId);

        return tags;
    }
}
