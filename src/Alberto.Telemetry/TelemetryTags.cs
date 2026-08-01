using System.Diagnostics;

namespace Alberto.Telemetry;

/// <summary>
/// Builds the tag set every consume metric carries.
/// </summary>
/// <remarks>
/// Delegates to <see cref="ProcessorTags.ForModule"/>, which lives in the core assembly and is
/// the single authoritative definition of the tag shape. Having one definition guarantees that
/// the instruments emitted by this package (events processed, processing errors, processing
/// duration) carry identical tag sets to those emitted by core middleware (dead-letters, retries,
/// processor lag), so dashboards can join them without a label transform.
/// </remarks>
internal static class TelemetryTags
{
    internal static TagList ForModule(string processorId, string moduleKey) =>
        ProcessorTags.ForModule(processorId, moduleKey);
}
