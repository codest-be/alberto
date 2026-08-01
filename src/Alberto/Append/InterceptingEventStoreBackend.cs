namespace Alberto.Append;

/// <summary>
/// Decorator that executes the append interceptor pipeline around backend operations.
/// All read operations are passed through to the inner backend.
/// </summary>
internal sealed class InterceptingEventStoreBackend(IEventStoreBackend inner, IAppendInterceptorPipeline pipeline)
    : IEventStoreBackend, IEventStoreHeadBackend
{
    private readonly IEventStoreBackend _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IAppendInterceptorPipeline _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    private readonly IEventStoreHeadBackend _innerHead = inner as IEventStoreHeadBackend
        ?? throw new ArgumentException(
            "Inner backend must implement IEventStoreHeadBackend", nameof(inner));

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var context = new AppendContext
        {
            Events = events.ToList(),
            DcbQuery = dcbQuery,
            ExpectedPosition = expectedPosition
        };

        return _pipeline.ExecuteAsync(
            context,
            ctx => _inner.AppendAsync(
                ctx.Events,
                ctx.DcbQuery,
                ctx.ExpectedPosition,
                cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => _inner.StreamAsync(query, afterPosition, limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => _inner.StreamAllAsync(afterPosition, limit, cancellationToken);

    /// <inheritdoc />
    public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
        => _inner.GetLastPositionAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> GetPositionsAsync(
        long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        => _innerHead.GetPositionsAsync(afterPosition, windowSize, cancellationToken);

    /// <inheritdoc />
    public Task<long> GetStableHeadAsync(
        long afterPosition, CancellationToken cancellationToken = default)
        => _innerHead.GetStableHeadAsync(afterPosition, cancellationToken);
}
