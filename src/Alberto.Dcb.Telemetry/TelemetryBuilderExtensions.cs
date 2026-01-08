using Alberto.Dcb.Subscriptions.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Extension methods for adding telemetry to Alberto.Dcb modules.
/// </summary>
public static class TelemetryBuilderExtensions
{
    /// <summary>
    /// Adds OpenTelemetry instrumentation to the module.
    /// Includes tracing and metrics for event processing.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithTelemetry(this DcbModuleBuilder builder)
    {
        builder.Services.AddKeyedSingleton<IConsumeFilter, TelemetryConsumeFilter>(builder.ModuleKey);
        return builder;
    }
}
