using System.Diagnostics;
using Alberto.Diagnostics;
using Alberto.Telemetry;

namespace Alberto.Append;

/// <summary>
/// Decorator that executes the append interceptor pipeline around backend operations.
/// Reads pass through to the inner backend, under a span when someone is listening.
/// </summary>
/// <param name="inner">The backend whose operations are wrapped.</param>
/// <param name="pipeline">The append interceptors run around each append.</param>
/// <param name="dbSystem">
/// The <c>db.system.name</c> to put on read spans, or null when the backend is not a database.
/// </param>
internal sealed class InterceptingEventStoreBackend(
    IEventStoreBackend inner,
    IAppendInterceptorPipeline pipeline,
    string? dbSystem = null)
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
                StampTraceContext(ctx.Events),
                ctx.DcbQuery,
                ctx.ExpectedPosition,
                cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Stamps the current trace context into each event's metadata. Done here, innermost in the
    /// pipeline and independent of telemetry being enabled, so the context is the Alberto.Append
    /// span's when there is one and the caller's ambient activity otherwise.
    /// </summary>
    private IReadOnlyList<IEventToPersist> StampTraceContext(IReadOnlyList<IEventToPersist> events)
    {
        // The append span is current here; it cannot know the backend, so the backend tags it.
        // Only our own append span: with telemetry off, Current is the caller's or the command root.
        if (dbSystem is not null && Activity.Current is { } append
            && ReferenceEquals(append.Source, AlbertoMetrics.Source)
            && append.Kind == ActivityKind.Producer
            && append.OperationName.StartsWith(AlbertoMetrics.AppendActivityName, StringComparison.Ordinal))
            append.SetTag("db.system.name", dbSystem);

        if (Activity.Current is not { } activity)
            return events;

        var context = new Dictionary<string, string>();
        TraceContextPropagation.Inject(activity, context);

        return events.Select(evt =>
        {
            var metadata = new Dictionary<string, string>(evt.Metadata);
            foreach (var (key, value) in context)
                metadata[key] = value;

            return (IEventToPersist)new EventToPersist
            {
                Id = evt.Id,
                EventType = evt.EventType,
                Tags = evt.Tags,
                EventData = evt.EventData,
                Metadata = metadata
            };
        }).ToList();
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => TracedRead(afterPosition, limit, () => _inner.StreamAsync(query, afterPosition, limit, cancellationToken));

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => TracedRead(afterPosition, limit, () => _inner.StreamAllAsync(afterPosition, limit, cancellationToken));

    private async Task<IReadOnlyCollection<IEventEnvelope>> TracedRead(
        long afterPosition,
        int? limit,
        Func<Task<IReadOnlyCollection<IEventEnvelope>>> read)
    {
        using var activity = AlbertoMetrics.Source.StartActivity(
            "alberto.read",
            dbSystem is null ? ActivityKind.Internal : ActivityKind.Client);
        if (activity is null)
            return await read();

        activity.SetTag("db.system.name", dbSystem);
        activity.SetTag("alberto.read.after_position", afterPosition);
        activity.SetTag("alberto.read.limit", limit);
        try
        {
            var events = await read();
            activity.SetTag("alberto.read.events", events.Count);
            return events;
        }
        catch (Exception exception)
        {
            activity.SetTag("error.type", exception.GetType().FullName);
            activity.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

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
