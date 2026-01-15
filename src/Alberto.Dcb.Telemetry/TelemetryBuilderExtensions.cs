using Alberto.Dcb.Append;
using Alberto.Dcb.Diagnostics;
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

        // Register consume filter with trace context provider for linking
        builder.Services.AddKeyedSingleton<IConsumeFilter>(moduleKey, (sp, _) =>
        {
            var provider = sp.GetKeyedService<ITraceContextProvider>(moduleKey);
            return new TelemetryConsumeFilter(provider);
        });

        return builder;
    }
}
