using EventStore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Telemetry;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelemetry(this IServiceCollection services)
    {
        return services.AddSingleton<IDiagnosticsEventListener, ActivityDiagnosticEventListener>();
    }
}