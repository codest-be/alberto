namespace Alberto.Dcb.Append;

/// <summary>
/// Decorator that executes the append interceptor pipeline around backend operations.
/// All read operations are passed through to the inner backend.
/// </summary>
internal sealed class InterceptingEventStoreBackend(IEventStoreBackend inner, IAppendInterceptorPipeline pipeline)
    : IEventStoreBackend
{
    private readonly IEventStoreBackend _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IAppendInterceptorPipeline _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> Append(
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
            ctx => _inner.Append(
                ctx.Events,
                ctx.DcbQuery,
                ctx.ExpectedPosition,
                cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => _inner.Stream(query, afterPosition, limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAll(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => _inner.StreamAll(afterPosition, limit, cancellationToken);

    /// <inheritdoc />
    public Task<long> GetLastPosition(CancellationToken cancellationToken = default)
        => _inner.GetLastPosition(cancellationToken);
}
