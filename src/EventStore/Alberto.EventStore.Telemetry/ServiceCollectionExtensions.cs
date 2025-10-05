using Alberto.EventStore.Subscriptions.Registration;
using OpenTelemetry.Trace;

namespace Alberto.EventStore.Telemetry;

public static class ServiceCollectionExtensions
{
    public static EventStoreModuleBuilder AddOpenTelemetry(this EventStoreModuleBuilder builder)
    {
        builder.AddTelemetry<ActivityDiagnosticEventListener>();
        return builder;
    }

    public static TracerProviderBuilder AddEventStoreTelemetry(this TracerProviderBuilder builder)
    {
        return builder.AddSource(AlbertoActivitySource.Name);
    }
}