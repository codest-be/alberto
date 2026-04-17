using Alberto.Dcb.Tenancy;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Decorator that adds tenant scoping to an event store backend.
/// Reads tenant ID from ITenantAccessor and delegates to the tenant-aware inner backend.
/// Registered when .WithTenancy() is called on the module builder.
/// </summary>
internal sealed class TenantEventStoreDecorator : IEventStoreBackend
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

    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantAccessor.TenantId;
        return _inner.StreamForTenant(tenantId, query, afterPosition, limit, cancellationToken);
    }

    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAll(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => _inner.StreamAllTenants(afterPosition, limit, cancellationToken);

    public Task<IReadOnlyCollection<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantAccessor.TenantId;
        return _inner.AppendForTenant(tenantId, events, dcbQuery, expectedPosition, cancellationToken);
    }

    public Task<long> GetLastPosition(CancellationToken cancellationToken = default)
        => _inner.GetLastPositionGlobal(cancellationToken);

    public Task<IReadOnlyList<long>> GetPositionsAsync(
        long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        => _inner.GetPositionsGlobalAsync(afterPosition, windowSize, cancellationToken);
}
