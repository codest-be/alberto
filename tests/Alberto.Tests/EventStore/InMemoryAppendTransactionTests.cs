using Alberto.InMemory;
using Alberto.Projections;
using Alberto.Tenancy;
using Xunit;

namespace Alberto.Tests;

public sealed class InMemoryAppendTransactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AppendAsync_ConcurrentAppendsToOneDocument_ReachATransactionalProjectionInLogOrder()
    {
        var projection = new RecordingProjection();
        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        projection.OnFirstCall = async () =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
        };
        var store = new EventStore(new InMemoryEventStoreBackend(), [projection]);

        var first = store.AppendAsync([Event("handled", "first")], cancellationToken: Ct);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = store.AppendAsync([Event("handled", "second")], cancellationToken: Ct);
        await Task.Delay(100, Ct);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["first", "second"], projection.Seen);
    }

    [Fact]
    public async Task AppendAsync_AProjectionThatThrows_LeavesNoEventsAndNoPositionTaken()
    {
        var backend = new InMemoryEventStoreBackend();
        var projection = new RecordingProjection { Fail = true };
        var store = new EventStore(backend, [projection]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync([Event("handled", "lost")], cancellationToken: Ct));

        Assert.Equal(0, await backend.GetLastPositionAsync(Ct));
        Assert.Empty(await backend.ReadAllAsync(0, 10, Ct));

        projection.Fail = false;
        var appended = await store.AppendAsync([Event("handled", "kept")], cancellationToken: Ct);

        Assert.Equal(1, appended.Single().GlobalPosition);
        Assert.Equal(["lost", "kept"], projection.Seen);
    }

    [Fact]
    public async Task AppendAsync_AProjectionThatThrows_DoesNotBlockTheNextAppend()
    {
        var backend = new InMemoryEventStoreBackend();
        var projection = new RecordingProjection { Fail = true };
        var store = new EventStore(backend, [projection]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync([Event("handled")], cancellationToken: Ct));

        var appended = await backend.AppendAsync([Event("other")], cancellationToken: Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Single(appended);
    }

    [Fact]
    public async Task AppendAsync_ADcbConflict_FailsBeforeTheProjectionRuns()
    {
        var backend = new InMemoryEventStoreBackend();
        var projection = new RecordingProjection();
        var store = new EventStore(backend, [projection]);
        var query = DcbQuery.ByTags("doc:1");
        await backend.AppendAsync([Event("other", "1")], cancellationToken: Ct);
        projection.Seen.Clear();

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            store.AppendAsync([Event("handled", "1")], query, expectedPosition: 0, Ct));

        Assert.Empty(projection.Seen);
        Assert.Equal(1, await backend.GetLastPositionAsync(Ct));
    }

    [Fact]
    public async Task ReadsAndHead_DuringASlowProjection_AreNotBlockedAndSeeNothingOfThePendingEvents()
    {
        var backend = new InMemoryEventStoreBackend();
        var projection = new RecordingProjection();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        projection.OnFirstCall = async () =>
        {
            entered.SetResult();
            await release.Task;
        };
        var store = new EventStore(backend, [projection]);

        var append = store.AppendAsync([Event("handled")], cancellationToken: Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var head = await backend.GetLastPositionAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var all = await backend.ReadAllAsync(0, 10, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var snapshot = await backend.GetHeadSnapshotAsync(0, 10, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(0, head);
        Assert.Empty(all);
        Assert.Empty(snapshot.Window.Positions);

        release.SetResult();
        await append;
        Assert.Equal(1, await backend.GetLastPositionAsync(Ct));
    }

    [Fact]
    public async Task AppendAsync_APlainAppend_WaitsForAnInFlightTransactionalAppend()
    {
        var backend = new InMemoryEventStoreBackend();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var transactional = backend.AppendAsync(
            [Event("handled", "first")], dcbQuery: null, expectedPosition: null,
            async (_, _, _) =>
            {
                entered.SetResult();
                await release.Task;
            },
            Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var plain = backend.AppendAsync([Event("other", "second")], cancellationToken: Ct);
        await Task.Delay(100, Ct);
        Assert.False(plain.IsCompleted);

        release.SetResult();
        var first = await transactional;
        var second = await plain;

        Assert.Equal(1, first.Single().GlobalPosition);
        Assert.Equal(2, second.Single().GlobalPosition);
    }

    [Fact]
    public async Task AppendAsync_AProjectionThatAppendsToTheSameBackend_WaitsOnTheGateUntilCancelled()
    {
        var backend = new InMemoryEventStoreBackend();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.AppendAsync(
            [Event("handled")], dcbQuery: null, expectedPosition: null,
            (_, _, token) => backend.AppendAsync([Event("nested")], cancellationToken: token),
            timeout.Token));

        Assert.Equal(0, await backend.GetLastPositionAsync(Ct));
    }

    [Fact]
    public async Task AppendAsync_OnATenantScopedBackend_HandsTheProjectionTheTenantsEventsBeforePublishing()
    {
        var inner = new InMemoryEventStoreBackend();
        var decorator = new InMemoryTenantEventStoreDecorator(inner, new FixedTenant("acme"));
        var projection = new RecordingProjection();
        var store = new EventStore(decorator, [projection]);

        await store.AppendAsync([Event("handled", "tenant-event")], cancellationToken: Ct);

        Assert.Equal(["tenant-event"], projection.Seen);
        Assert.Equal(["acme"], projection.TenantIds);
        Assert.Equal(1, await inner.GetLastPositionAsync(Ct));
    }

    [Fact]
    public async Task AppendAsync_OnATenantScopedBackend_AnotherTenantsAppendWaitsForTheGate()
    {
        var inner = new InMemoryEventStoreBackend();
        var acme = new InMemoryTenantEventStoreDecorator(inner, new FixedTenant("acme"));
        var globex = new InMemoryTenantEventStoreDecorator(inner, new FixedTenant("globex"));
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var first = acme.AppendAsync(
            [Event("handled")], dcbQuery: null, expectedPosition: null,
            async (_, _, _) =>
            {
                entered.SetResult();
                await release.Task;
            },
            Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var second = globex.AppendAsync([Event("other")], cancellationToken: Ct);
        await Task.Delay(100, Ct);
        Assert.False(second.IsCompleted);

        release.SetResult();
        Assert.Equal(1, (await first).Single().GlobalPosition);
        Assert.Equal(2, (await second).Single().GlobalPosition);
    }

    private static EventToPersist Event(string type, string data = "x") => new()
    {
        EventType = new EventType(type),
        Tags = [new EventTag("doc", data)],
        EventData = $"\"{data}\"",
    };

    private sealed class FixedTenant(string tenantId) : ITenantAccessor
    {
        public string TenantId => tenantId;
        public string? TenantIdOrDefault => tenantId;
        public bool HasTenant => true;
    }

    private sealed class RecordingProjection : IInlineProjection
    {
        private int _calls;

        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { "handled" };

        public List<string> Seen { get; } = [];

        public List<string?> TenantIds { get; } = [];

        public bool Fail { get; set; }

        public Func<Task>? OnFirstCall { get; set; }

        public Task ProcessAsync(
            IReadOnlyList<IEventEnvelope> events, AppendTransaction transaction, CancellationToken cancellationToken = default)
            => Record(events);

        private async Task Record(IReadOnlyList<IEventEnvelope> events)
        {
            if (Interlocked.Increment(ref _calls) == 1 && OnFirstCall is not null)
                await OnFirstCall();
            lock (Seen)
            {
                Seen.AddRange(events.Select(e => e.EventData.Trim('"')));
                TenantIds.AddRange(events.Select(e => e.TenantId));
            }

            if (Fail)
                throw new InvalidOperationException("projection failed");
        }
    }
}
