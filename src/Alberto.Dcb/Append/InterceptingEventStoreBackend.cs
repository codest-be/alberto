namespace Alberto.Dcb.Append;

/// <summary>
/// Decorator that executes the append interceptor pipeline around backend operations.
/// All read operations are passed through to the inner backend.
/// </summary>
internal sealed class InterceptingEventStoreBackend : IEventStoreBackend
{
    private readonly IEventStoreBackend _inner;
    private readonly IAppendInterceptorPipeline _pipeline;

    public InterceptingEventStoreBackend(IEventStoreBackend inner, IAppendInterceptorPipeline pipeline)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> Append(
        string tenantId,
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var context = new AppendContext
        {
            TenantId = tenantId,
            Events = events.ToList(),
            DcbQuery = dcbQuery,
            ExpectedPosition = expectedPosition
        };

        return _pipeline.ExecuteAsync(
            context,
            ctx => _inner.Append(
                ctx.TenantId,
                ctx.Events,
                ctx.DcbQuery,
                ctx.ExpectedPosition,
                cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        string tenantId,
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => _inner.Stream(tenantId, query, afterPosition, limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamGlobal(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => _inner.StreamGlobal(afterPosition, limit, cancellationToken);

    /// <inheritdoc />
    public Task<long> GetLastPosition(
        string tenantId,
        CancellationToken cancellationToken = default)
        => _inner.GetLastPosition(tenantId, cancellationToken);

    /// <inheritdoc />
    public Task<long> GetLastPositionGlobal(
        CancellationToken cancellationToken = default)
        => _inner.GetLastPositionGlobal(cancellationToken);
}
