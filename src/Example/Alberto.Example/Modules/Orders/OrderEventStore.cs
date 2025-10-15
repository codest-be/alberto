using Alberto.EventStore;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Subscriptions.Channel;

namespace Alberto.Example.Modules.Orders;

public class OrderEventStore(
    ITenantContext tenantContext,
    IEventStoreBackend backend,
    ChannelSubscriptionRegistry channelRegistry,
    IDiagnosticsEventListener? diagnostics = null)
    : EventStoreFactory(tenantContext, backend, channelRegistry, diagnostics);