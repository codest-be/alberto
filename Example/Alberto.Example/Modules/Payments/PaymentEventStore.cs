using Alberto.EventStore;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.MultiTenant;

namespace Alberto.Example.Modules.Payments;

public class PaymentEventStore(
    ITenantContext tenantContext,
    IEventStoreBackend backend,
    IDiagnosticsEventListener diagnostics) : EventStoreFactory(tenantContext, backend, diagnostics);