using Alberto.EventStore;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.MultiTenant;

namespace Alberto.Example.Modules.Orders;

public class OrderEventStoreFactory(
    ITenantContext tenantContext,
    IEventStoreBackend backend,
    IDiagnosticsEventListener? diagnostics = null) : EventStoreFactory(tenantContext, backend, diagnostics);