using Alberto.EventStore;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Subscriptions.Channel;

namespace Alberto.Example.Modules.Payments;

public class PaymentEventStore(
    ITenantContext tenantContext,
    IEventStoreBackend backend,
    ChannelSubscriptionRegistry channelRegistry,
    IDiagnosticsEventListener diagnostics) : EventStoreFactory(tenantContext, backend, channelRegistry, diagnostics);