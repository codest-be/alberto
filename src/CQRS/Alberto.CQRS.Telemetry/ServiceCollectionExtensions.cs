using Alberto.CQRS.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Alberto.CQRS.Telemetry;

/// <summary>
/// Extension methods for configuring Alberto CQRS telemetry.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds OpenTelemetry Activity-based telemetry for Alberto CQRS operations.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAlbertoCQRSTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<IDiagnosticsEventListener, ActivityDiagnosticEventListener>();
        return services;
    }

    /// <summary>
    /// Adds Alberto CQRS instrumentation to the OpenTelemetry tracer provider.
    /// This registers the Alberto.CQRS ActivitySource for distributed tracing.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <returns>The tracer provider builder for chaining.</returns>
    public static TracerProviderBuilder AddAlbertoCQRSInstrumentation(this TracerProviderBuilder builder)
    {
        return builder.AddSource(AlbertoCQRSActivitySource.Name);
    }
}