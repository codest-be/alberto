using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Extension methods for registering Alberto telemetry with OpenTelemetry.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Alberto.Dcb tracing to an OpenTelemetry <see cref="TracerProviderBuilder"/>.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Use this method when configuring a <see cref="TracerProviderBuilder"/> outside of the
    /// .NET generic host (for example, in a standalone console app that builds its own
    /// <c>TracerProvider</c>). In hosted applications that call <c>.WithTelemetry()</c>,
    /// <c>AddAlbertoInstrumentation()</c> is a no-op duplicate — <c>AddSource</c> is idempotent,
    /// so calling both is safe.
    /// </para>
    /// </remarks>
    public static TracerProviderBuilder AddAlbertoInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(AlbertoMetrics.Name);
    }

    /// <summary>
    /// Adds Alberto.Dcb metrics to an OpenTelemetry <see cref="MeterProviderBuilder"/>.
    /// </summary>
    /// <param name="builder">The meter provider builder.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Use this method when configuring a <see cref="MeterProviderBuilder"/> outside of the
    /// .NET generic host (for example, in a standalone console app that builds its own
    /// <c>MeterProvider</c>). In hosted applications that call <c>.WithTelemetry()</c>,
    /// <c>AddAlbertoInstrumentation()</c> is a no-op duplicate — <c>AddMeter</c> is idempotent,
    /// so calling both is safe.
    /// </para>
    /// </remarks>
    public static MeterProviderBuilder AddAlbertoInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(AlbertoMetrics.Name);
    }
}
