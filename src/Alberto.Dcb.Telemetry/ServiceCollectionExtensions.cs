using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Extension methods for registering Alberto telemetry with OpenTelemetry.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Alberto.Dcb tracing to the OpenTelemetry TracerProvider.
    /// </summary>
    [Obsolete("Alberto registers its own activity source and meter from .WithTelemetry() when the " +
              "OpenTelemetry hosting integration is present. Call this only when configuring a " +
              "TracerProvider or MeterProvider outside the host.")]
    public static TracerProviderBuilder AddAlbertoInstrumentation(this TracerProviderBuilder builder)
    {
        return builder.AddSource(AlbertoMetrics.Name);
    }

    /// <summary>
    /// Adds Alberto.Dcb metrics to the OpenTelemetry MeterProvider.
    /// </summary>
    [Obsolete("Alberto registers its own activity source and meter from .WithTelemetry() when the " +
              "OpenTelemetry hosting integration is present. Call this only when configuring a " +
              "TracerProvider or MeterProvider outside the host.")]
    public static MeterProviderBuilder AddAlbertoInstrumentation(this MeterProviderBuilder builder)
    {
        return builder.AddMeter(AlbertoMetrics.Name);
    }
}
