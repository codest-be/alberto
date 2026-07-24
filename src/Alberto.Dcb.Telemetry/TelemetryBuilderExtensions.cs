using Alberto.Dcb.Append;
using Alberto.Dcb.Diagnostics;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable CS0618 // Task 12 removes DcbModuleBuilder.Services; delete this pragma then.

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Extension methods for adding telemetry to Alberto.Dcb modules.
/// </summary>
public static class TelemetryBuilderExtensions
{
    /// <summary>
    /// Adds OpenTelemetry instrumentation to the module.
    /// Includes distributed tracing for both append and consume operations,
    /// with trace context propagation through event metadata.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithTelemetry(this DcbModuleBuilder builder)
    {
        var moduleKey = builder.ModuleKey;

        // Register trace context provider for extracting trace IDs from metadata
        builder.Services.AddKeyedSingleton<ITraceContextProvider, ActivityTraceContextProvider>(moduleKey);

        // Register append interceptor for trace context enrichment
        builder.Services.AddKeyedSingleton<IAppendInterceptor, TelemetryAppendInterceptor>(moduleKey);

        // Register consume middleware that traces every processed event.
        // This is picked up by ControlLoopBuilder and runs as the outermost layer
        // in the consume pipeline (one span per event including all retries).
        builder.Services.AddKeyedSingleton<ConsumeMiddleware>(moduleKey, (sp, _) =>
        {
            var provider = sp.GetKeyedService<ITraceContextProvider>(moduleKey);
            return TelemetryConsumeMiddleware.Create(provider);
        });
        builder.Services.AddKeyedSingleton<BatchConsumeMiddleware>(moduleKey, (sp, _) =>
        {
            var provider = sp.GetKeyedService<ITraceContextProvider>(moduleKey);
            return TelemetryBatchConsumeMiddleware.Create(provider);
        });

        return builder;
    }
}
