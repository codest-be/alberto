using Alberto.Dcb.Tenancy;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Decorator that adds tenant scoping to an event store backend.
/// Reads tenant ID from ITenantAccessor and delegates to the tenant-aware inner backend.
/// Registered when .WithTenancy() is called on the module builder.
/// </summary>
internal sealed class TenantEventStoreDecorator : IEventStoreBackend, IEventStoreHeadBackend
{
    private readonly PostgresTenantEventStoreBackend _inner;
    private readonly ITenantAccessor _tenantAccessor;

    public TenantEventStoreDecorator(
        PostgresTenantEventStoreBackend inner,
        ITenantAccessor tenantAccessor)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
    }

    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantAccessor.TenantId;
        return _inner.StreamForTenant(tenantId, query, afterPosition, limit, cancellationToken);
    }

    // NOTE: P1.3 interim guard — full interface split (tenant-scoped store vs consumer feed)
    // is planned for the breaking-changes phase. Until then, guard the request-scoped path:
    // if a caller has an active tenant (i.e. this is a request-scoped context), StreamAllAsync would
    // silently return every tenant's events, bypassing isolation — throw instead.
    // The consumer-feed backend uses ConsumerTenantAccessor (HasTenant=false) and is allowed
    // to call StreamAllAsync; it legitimately streams across all tenants for background loops.
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (_tenantAccessor.HasTenant)
            throw new InvalidOperationException(
                "StreamAllAsync() is not permitted on the request-scoped tenant event store: it would " +
                "return events for all tenants, bypassing tenant isolation. " +
                "Use the consumer-feed backend (registered under the ':consumer' key) for " +
                "cross-tenant streaming from ControlLoops, or await the interface split that " +
                "will formally separate the two concerns.");

        return _inner.StreamAllTenants(afterPosition, limit, cancellationToken);
    }

    public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantAccessor.TenantId;
        return _inner.AppendForTenant(tenantId, events, dcbQuery, expectedPosition, cancellationToken);
    }

    public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
        => _inner.GetLastPositionGlobal(cancellationToken);

    public Task<IReadOnlyList<long>> GetPositionsAsync(
        long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        => _inner.GetPositionsGlobalAsync(afterPosition, windowSize, cancellationToken);

    public Task<long> GetStableHeadAsync(
        long afterPosition, CancellationToken cancellationToken = default)
        => _inner.GetStableHeadGlobalAsync(afterPosition, cancellationToken);
}
