using Alberto.InMemory;
using Alberto.Projections;
using Xunit;

namespace Alberto.Tests;

public sealed class TransactionalInlineProjectionTests
{
    [Fact]
    public async Task AppendAsync_RunsTheProjectionInsideTheAppendOnce()
    {
        var projection = new InTransactionProjection("handled");
        var backend = new InTransactionBackend(projection.Calls);
        var store = new EventStore(backend, [projection]);

        await store.AppendAsync([Event("handled")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["begin", "in-transaction", "commit"], projection.Calls);
    }

    [Fact]
    public async Task AppendAsync_KeepsTheTransactionOutOfAppendsTheProjectionIgnores()
    {
        var projection = new InTransactionProjection("handled");
        var backend = new InTransactionBackend(projection.Calls);
        var store = new EventStore(backend, [projection]);

        await store.AppendAsync([Event("unrelated")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["plain"], projection.Calls);
    }

    [Fact]
    public async Task AppendAsync_AProjectionThatThrowsInTheTransaction_FailsTheAppendWithoutCommitting()
    {
        var projection = new InTransactionProjection("handled") { Fail = true };
        var backend = new InTransactionBackend(projection.Calls);
        var store = new EventStore(backend, [projection]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync([Event("handled")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(["begin", "in-transaction"], projection.Calls);
    }

    private static EventToPersist Event(string type) => new()
    {
        EventType = new EventType(type),
        Tags = [],
        EventData = "{}",
    };

    private sealed class InTransactionProjection(string handled) : IInlineProjection
    {
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { handled };

        public List<string> Calls { get; } = [];

        public bool Fail { get; init; }

        public Task ProcessAsync(
            IReadOnlyList<IEventEnvelope> events, AppendTransaction transaction, CancellationToken cancellationToken = default)
        {
            Calls.Add("in-transaction");
            return Fail ? throw new InvalidOperationException("projection failed") : Task.CompletedTask;
        }
    }

    private sealed class InTransactionBackend(List<string> calls) : IEventStoreBackend
    {
        private readonly InMemoryEventStoreBackend _inner = new();

        public async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events, DcbQuery? dcbQuery = null, long? expectedPosition = null,
            CancellationToken cancellationToken = default)
        {
            calls.Add("plain");
            return await _inner.AppendAsync(events, dcbQuery, expectedPosition, cancellationToken);
        }

        public async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events, DcbQuery? dcbQuery, long? expectedPosition,
            Func<IReadOnlyList<IEventEnvelope>, AppendTransaction, CancellationToken, Task> inTransaction,
            CancellationToken cancellationToken = default)
        {
            calls.Add("begin");
            var appended = await _inner.AppendAsync(events, dcbQuery, expectedPosition, cancellationToken);
            await inTransaction(appended.ToList(), AppendTransaction.None, cancellationToken);
            calls.Add("commit");
            return appended;
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> ReadAsync(DcbQuery query, long afterPosition, int limit, CancellationToken cancellationToken = default) => _inner.ReadAsync(query, afterPosition, limit, cancellationToken);
        public Task<IReadOnlyCollection<IEventEnvelope>> ReadAllAsync(long afterPosition, int limit, CancellationToken cancellationToken = default) => _inner.ReadAllAsync(afterPosition, limit, cancellationToken);
        public Task<IReadOnlyCollection<IEventEnvelope>> ReadAllOfTypesAsync(IReadOnlySet<string> eventTypes, long afterPosition, int limit, CancellationToken cancellationToken = default) => _inner.ReadAllOfTypesAsync(eventTypes, afterPosition, limit, cancellationToken);
        public IAsyncEnumerable<IEventEnvelope> StreamAsync(DcbQuery query, long afterPosition = 0, CancellationToken cancellationToken = default) => _inner.StreamAsync(query, afterPosition, cancellationToken);
        public IAsyncEnumerable<IEventEnvelope> StreamAllAsync(long afterPosition = 0, CancellationToken cancellationToken = default) => _inner.StreamAllAsync(afterPosition, cancellationToken);
        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default) => _inner.GetLastPositionAsync(cancellationToken);
    }
}
