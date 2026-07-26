using System.Diagnostics;
using Alberto.Dcb.Tenancy;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Builds the tag set that every processor metric carries.
/// </summary>
/// <remarks>
/// A sharded module's processors run under the physical key <c>module#shard</c>, which is an
/// implementation detail nobody wants in a dashboard — and worse, one that splits a module's
/// throughput into series that no query can add back up. The module key and the shard id are
/// therefore reported as separate tags: sum over <c>shard</c> for the module's total, group by
/// it to see one database lagging.
///
/// This is the single authoritative definition of the tag set shared across all consume-path
/// instruments. <c>Alberto.Dcb.Telemetry</c>'s <c>TelemetryTags</c> delegates here so that
/// core middleware (dead-letters, retries, processor lag) and the optional telemetry package
/// (events processed, processing errors, processing duration) always carry identical shapes and
/// can be joined in a dashboard without a separate label transform.
///
/// Kept <see langword="internal"/> because the tag contract is an Alberto abstraction, not a
/// consumer extension point. <c>Alberto.Dcb.Telemetry</c> and <c>Alberto.Dcb.Tests</c> both
/// receive access via <c>InternalsVisibleTo</c> in <c>Alberto.Dcb.csproj</c>.
/// </remarks>
internal static class ProcessorTags
{
    /// <summary>
    /// Returns a tag list carrying <c>processor</c>, <c>module</c>, and — for sharded modules
    /// — <c>shard</c>. Pass <paramref name="moduleKey"/> as the raw physical key (which may be
    /// <c>module#shard</c>); the split happens here so callers never have to think about it.
    /// </summary>
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
