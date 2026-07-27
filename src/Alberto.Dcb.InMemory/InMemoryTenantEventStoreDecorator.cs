using Alberto.Dcb.Tenancy;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// Decorator that adds tenant scoping to the in-memory event store backend.
/// Reads tenant ID from <see cref="ITenantAccessor"/> and delegates to the
/// tenant-aware methods on <see cref="InMemoryEventStoreBackend"/>.
/// Registered when <c>.WithTenancy()</c> is called on the module builder.
/// </summary>
/// <remarks>
/// This is the in-memory counterpart of <c>TenantEventStoreDecorator</c> in the Postgres
/// package and follows the same contract:
/// <list type="bullet">
/// <item>Registered as <em>scoped</em> (per request) because it reads from the scoped
/// <see cref="ITenantAccessor"/>, while the underlying <see cref="InMemoryEventStoreBackend"/>
/// stays a singleton.</item>
/// <item>A separate singleton instance using <c>ConsumerInMemoryTenantAccessor</c> is registered
/// under the <c>:consumer</c> key for ControlLoop singletons that stream all tenants.</item>
/// </list>
/// </remarks>
internal sealed class InMemoryTenantEventStoreDecorator(
    InMemoryEventStoreBackend inner,
    ITenantAccessor tenantAccessor) : IEventStoreBackend, IEventStoreHeadBackend
{
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantAccessor.TenantId;
        return inner.StreamForTenant(tenantId, query, afterPosition, limit, cancellationToken);
    }

    // NOTE: P1.3 interim guard — mirrors TenantEventStoreDecorator exactly.
    // A request-scoped caller has an active tenant and must not read all tenants'
    // events. ControlLoops use ConsumerInMemoryTenantAccessor (HasTenant = false)
    // and are allowed through to StreamAllTenants for cross-tenant streaming.
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (tenantAccessor.HasTenant)
            throw new InvalidOperationException(
                "StreamAllAsync() is not permitted on the request-scoped tenant event store: it would " +
                "return events for all tenants, bypassing tenant isolation. " +
                "Use the consumer-feed backend (registered under the ':consumer' key) for " +
                "cross-tenant streaming from ControlLoops, or await the interface split that " +
                "will formally separate the two concerns.");

        return inner.StreamAllTenants(afterPosition, limit, cancellationToken);
    }

    public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantAccessor.TenantId;
        return inner.AppendForTenant(tenantId, events, dcbQuery, expectedPosition, cancellationToken);
    }

    public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
        => inner.GetLastPositionAsync(cancellationToken);

    public Task<IReadOnlyList<long>> GetPositionsAsync(
        long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        => inner.GetPositionsAsync(afterPosition, windowSize, cancellationToken);

    // In-memory has no in-flight transactions, so there is no stable-head barrier.
    // The default implementation (long.MaxValue) is inherited from IEventStoreHeadBackend,
    // telling the ControlLoop it can safely advance to whatever position GetPositionsAsync returns.
}

