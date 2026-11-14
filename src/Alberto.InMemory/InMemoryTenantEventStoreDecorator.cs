using Alberto.Tenancy;

namespace Alberto.InMemory;

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
    public Task<IReadOnlyCollection<IEventEnvelope>> ReadAsync(
        DcbQuery query,
        long afterPosition,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantAccessor.TenantId;
        return inner.ReadForTenant(tenantId, query, afterPosition, limit, cancellationToken);
    }

    public IAsyncEnumerable<IEventEnvelope> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        CancellationToken cancellationToken = default)
        => inner.StreamForTenant(tenantAccessor.TenantId, query, afterPosition, cancellationToken);

    // NOTE: P1.3 interim guard — mirrors TenantEventStoreDecorator exactly.
    // A request-scoped caller has an active tenant and must not read all tenants'
    // events. ControlLoops use ConsumerInMemoryTenantAccessor (HasTenant = false)
    // and are allowed through to ReadAllTenants for cross-tenant reading.
    public Task<IReadOnlyCollection<IEventEnvelope>> ReadAllAsync(
        long afterPosition,
        int limit,
        CancellationToken cancellationToken = default)
    {
        GuardAllTenants(nameof(ReadAllAsync));
        return inner.ReadAllTenants(afterPosition, limit, cancellationToken);
    }

    public IAsyncEnumerable<IEventEnvelope> StreamAllAsync(
        long afterPosition = 0,
        CancellationToken cancellationToken = default)
    {
        GuardAllTenants(nameof(StreamAllAsync));
        return inner.StreamAsync(DcbQuery.All, afterPosition, cancellationToken);
    }

    // Same guard as ReadAllAsync, which it falls back to when a tenant is active.
    public Task<IReadOnlyCollection<IEventEnvelope>> ReadAllOfTypesAsync(
        IReadOnlySet<string> eventTypes,
        long afterPosition,
        int limit,
        CancellationToken cancellationToken = default)
        => tenantAccessor.HasTenant
            ? ReadAllAsync(afterPosition, limit, cancellationToken)
            : inner.ReadAllOfTypesAsync(eventTypes, afterPosition, limit, cancellationToken);

    private void GuardAllTenants(string method)
    {
        if (tenantAccessor.HasTenant)
            throw new InvalidOperationException(
                $"{method}() is not permitted on the request-scoped tenant event store: it would " +
                "return events for all tenants, bypassing tenant isolation. " +
                "Use the consumer-feed backend (registered under the ':consumer' key) for " +
                "cross-tenant reading from ControlLoops, or await the interface split that " +
                "will formally separate the two concerns.");
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

