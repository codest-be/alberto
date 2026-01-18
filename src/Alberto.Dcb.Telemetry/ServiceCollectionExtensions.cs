using Microsoft.Extensions.DependencyInjection;
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
    public static TracerProviderBuilder AddAlbertoInstrumentation(this TracerProviderBuilder builder)
    {
        return builder.AddSource(AlbertoMetrics.Name);
    }

    /// <summary>
    /// Adds Alberto.Dcb metrics to the OpenTelemetry MeterProvider.
    /// </summary>
    public static MeterProviderBuilder AddAlbertoInstrumentation(this MeterProviderBuilder builder)
    {
        return builder.AddMeter(AlbertoMetrics.Name);
    }
}
