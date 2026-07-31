using Alberto.Append;
using Alberto.Configuration;
using Alberto.Diagnostics;
using Alberto.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Alberto.Telemetry;

/// <summary>
/// Extension methods for adding telemetry to Alberto modules.
/// </summary>
public static class TelemetryBuilderExtensions
{
    /// <summary>
    /// Instruments this module: appends interceptors, consumes middleware, and — when the
    /// application uses the OpenTelemetry hosting integration — registers Alberto's activity
    /// source and meter automatically, so no separate <c>AddAlbertoInstrumentation()</c>
    /// call is needed.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">Transforms the telemetry options with a <c>with</c> expression.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithTelemetry(
        this DcbModuleBuilder builder,
        Func<TelemetryOptions, TelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Configure(d => d with
        {
            TelemetryEnabled = true,
            Telemetry = configure is null
                ? d.Telemetry
                : configure(d.Telemetry)
                  ?? throw new InvalidOperationException("WithTelemetry configurator returned null."),
        });

        return builder.Register(context =>
        {
            var services = context.Services;
            var moduleKey = context.ModuleKey;

            // Register Alberto's activity source and meter with the OpenTelemetry hosting
            // integration. When the application calls AddOpenTelemetry() independently,
            // these calls add to the same builder (additive and idempotent — AddSource and
            // AddMeter deduplicate by name). When the application never calls
            // AddOpenTelemetry(), this installs the SDK with no exporters, which is
            // effectively inert: no traces are emitted without an exporter.
            services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddSource(AlbertoMetrics.Name))
                .WithMetrics(metrics => metrics.AddMeter(AlbertoMetrics.Name));

            // Register trace context provider and append interceptor guarded by the resolved
            // Enabled flag — consistent with the consume middleware factories below.
            // Resolved once at DI resolution time (factory lambda), matching the pattern
            // used at lines 64–80.
            services.AddKeyedSingleton<ITraceContextProvider>(moduleKey, (sp, _) =>
            {
                var enabled = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
                    .Get(moduleKey).Telemetry.Enabled;
                return enabled
                    ? (ITraceContextProvider)new ActivityTraceContextProvider()
                    : NoOpTraceContextProvider.Instance;
            });

            services.AddKeyedSingleton<IAppendInterceptor>(moduleKey, (sp, _) =>
            {
                var telemetry = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
                    .Get(moduleKey).Telemetry;
                return telemetry.Enabled
                    ? (IAppendInterceptor)new TelemetryAppendInterceptor(telemetry.RecordEventTagValues)
                    : NoOpAppendInterceptor.Instance;
            });

            // Register consume middleware that traces every processed event.
            // Guard on the resolved options so Telemetry:Enabled = false in configuration
            // genuinely turns instrumentation off.
            services.AddKeyedSingleton<ConsumeMiddleware>(moduleKey, (sp, _) =>
            {
                var enabled = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
                    .Get(moduleKey).Telemetry.Enabled;
                if (!enabled) return (_, next) => next();
                var provider = sp.GetKeyedService<ITraceContextProvider>(moduleKey);
                return TelemetryConsumeMiddleware.Create(provider);
            });

            services.AddKeyedSingleton<BatchConsumeMiddleware>(moduleKey, (sp, _) =>
            {
                var enabled = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
                    .Get(moduleKey).Telemetry.Enabled;
                if (!enabled) return (_, next) => next();
                var provider = sp.GetKeyedService<ITraceContextProvider>(moduleKey);
                return TelemetryBatchConsumeMiddleware.Create(provider);
            });
        });
    }
}
