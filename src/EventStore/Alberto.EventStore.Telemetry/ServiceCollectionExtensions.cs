using Alberto.EventStore.Subscriptions.Registration;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Alberto.EventStore.Telemetry;

public static class ServiceCollectionExtensions
{
    public static EventStoreModuleBuilder AddOpenTelemetry(this EventStoreModuleBuilder builder)
    {
        builder.AddDiagnosticEventListener<ActivityDiagnosticEventListener>();
        builder.AddTraceContextProvider<ActivityTraceContextProvider>();

        return builder;
    }

    public static TracerProviderBuilder AddAlbertoInstrumentation(this TracerProviderBuilder builder)
    {
        return builder.AddSource(AlbertoActivitySource.Name);
    }

    public static MeterProviderBuilder AddAlbertoInstrumentation(this MeterProviderBuilder builder)
    {
        return builder.AddMeter(AlbertoMeter.Name);
    }
}