using Alberto.Commands;
using System.Text.Json;
using Alberto.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests;

public sealed class CommandPipelineTests
{
    [EventType("pipeline-order-created")]
    private sealed record OrderCreated : IEvent
    {
        [Tag("order")] public required Guid OrderId { get; init; }
    }

    [EventType("pipeline-order-reserved")]
    private sealed record OrderReserved : IEvent
    {
        [Tag("order")] public required Guid OrderId { get; init; }
    }

    [EventType("pipeline-order-confirmed")]
    private sealed record OrderConfirmed : IEvent
    {
        [Tag("order")] public required Guid OrderId { get; init; }
    }

    private sealed record ConfirmOrder(Guid OrderId);

    [EventType("pipeline-scope-probe")]
    private sealed record ScopeProbe : IEvent;

    private sealed record OrderState
    {
        public static readonly OrderState Initial = new();
        public int EventCount { get; init; }
    }

    private sealed class OrderStateEvolver : Evolver<OrderState>,
        IEvolve<OrderState, OrderCreated>,
        IEvolve<OrderState, OrderReserved>,
        IEvolve<OrderState, OrderConfirmed>
    {
        public OrderState Apply(OrderState state, OrderCreated @event) => Count(state);
        public OrderState Apply(OrderState state, OrderReserved @event) => Count(state);
        public OrderState Apply(OrderState state, OrderConfirmed @event) => Count(state);

        private static OrderState Count(OrderState state) => state with { EventCount = state.EventCount + 1 };
    }

    // ---------------------------------------------------------------------
    // Bound commits: the boundary comes from Load, so Commit(ct) needs nothing else.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Commit_AfterLoad_ChecksAgainstThePositionLoadObserved()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();
        await fixture.Append(new OrderCreated { OrderId = orderId });

        // Appending from inside the fold lands the event *after* Load streamed the
        // boundary, so the position it captured is already stale by the time we commit.
        await Assert.ThrowsAsync<DcbConflictException>(() =>
            fixture.Store
                .Handle(new ConfirmOrder(orderId))
                .Load(
                    Boundary(orderId),
                    OrderState.Initial,
                    (state, _) =>
                    {
                        fixture.Append(new OrderReserved { OrderId = orderId }).GetAwaiter().GetResult();
                        return state with { EventCount = state.EventCount + 1 };
                    })
                .Decide((command, _) => Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId }))
                .Commit(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Commit_WithAnEvolver_ReconstitutesStateAndCapturesThePosition()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();
        await fixture.Append(new OrderCreated { OrderId = orderId });
        await fixture.Append(new OrderReserved { OrderId = orderId });

        var observed = 0;

        var result = await fixture.Store
            .Handle(new ConfirmOrder(orderId))
            .Load(Boundary(orderId), new OrderStateEvolver())
            .Decide((command, state) =>
            {
                observed = state.EventCount;
                return Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId });
            })
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, observed);
        Assert.Equal(3, await fixture.EventStore.GetLastPositionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryCommit_ReturnsTheConflictAsAProblemInsteadOfThrowing()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();
        await fixture.Append(new OrderCreated { OrderId = orderId });

        var result = await fixture.Store
            .Handle(new ConfirmOrder(orderId))
            .Load(
                Boundary(orderId),
                OrderState.Initial,
                (state, _) =>
                {
                    fixture.Append(new OrderReserved { OrderId = orderId }).GetAwaiter().GetResult();
                    return state;
                })
            .Decide((command, _) => Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId }))
            .TryCommit(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dcb.conflict", Assert.Single(result.Problems).Code);
    }

    [Fact]
    public async Task RetryOnConflict_RereadsTheBoundaryAndDecidesAgain()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();
        await fixture.Append(new OrderCreated { OrderId = orderId });

        var folds = 0;
        var decisions = 0;

        var result = await fixture.Store
            .Handle(new ConfirmOrder(orderId))
            .Load(
                Boundary(orderId),
                OrderState.Initial,
                (state, _) =>
                {
                    // Only the first attempt races an append in; the retry finds a quiet boundary.
                    if (Interlocked.Increment(ref folds) == 1)
                        fixture.Append(new OrderReserved { OrderId = orderId }).GetAwaiter().GetResult();

                    return state with { EventCount = state.EventCount + 1 };
                })
            .Decide((command, _) =>
            {
                decisions++;
                return Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId });
            })
            .RetryOnConflict(3)
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, decisions);
    }

    [Fact]
    public async Task RetryOnConflict_DoesNotRepeatEnrichment()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();
        await fixture.Append(new OrderCreated { OrderId = orderId });

        var enrichments = 0;
        var decisions = 0;
        var folds = 0;

        var result = await fixture.Store
            .Handle(orderId)
            .Enrich((id, _) =>
            {
                enrichments++;
                return Task.FromResult(new ConfirmOrder(id));
            })
            .Load(
                command => Boundary(command.OrderId),
                OrderState.Initial,
                (state, _) =>
                {
                    if (Interlocked.Increment(ref folds) == 1)
                        fixture.Append(new OrderReserved { OrderId = orderId }).GetAwaiter().GetResult();

                    return state;
                })
            .Decide((command, _) =>
            {
                decisions++;
                return Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId });
            })
            .RetryOnConflict(3)
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, decisions);
        Assert.Equal(1, enrichments);
    }

    [Fact]
    public async Task RetryOnConflict_GivesUpAfterTheConfiguredNumberOfAttempts()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();
        await fixture.Append(new OrderCreated { OrderId = orderId });

        var folds = 0;

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            fixture.Store
                .Handle(new ConfirmOrder(orderId))
                .Load(
                    Boundary(orderId),
                    OrderState.Initial,
                    (state, _) =>
                    {
                        Interlocked.Increment(ref folds);
                        fixture.Append(new OrderReserved { OrderId = orderId }).GetAwaiter().GetResult();
                        return state;
                    })
                .Decide((command, _) => Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId }))
                .RetryOnConflict(2)
                .Commit(TestContext.Current.CancellationToken));

        // Two attempts, and the boundary grew by one event before each, so the second
        // fold runs over two events: 1 + 2 applications.
        Assert.Equal(3, folds);
    }

    [Fact]
    public void RetryOnConflict_RejectsANonPositiveAttemptCount()
    {
        var fixture = new Fixture();
        var pipeline = fixture.Store
            .Handle(new ConfirmOrder(Guid.NewGuid()))
            .Load(Boundary(Guid.NewGuid()), OrderState.Initial, (state, _) => state)
            .Decide((_, _) => Decision.Succeed());

        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.RetryOnConflict(0));
    }

    // ---------------------------------------------------------------------
    // Validation and enrichment
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Validate_ShortCircuitsWithoutTouchingTheStore()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();
        var loaded = false;

        var result = await fixture.Store
            .Handle(new ConfirmOrder(orderId))
            .Validate(_ => Problem.Create("order.rejected", "Nope"))
            .Load(
                Boundary(orderId),
                OrderState.Initial,
                (state, _) =>
                {
                    loaded = true;
                    return state;
                })
            .Decide((_, _) => Decision.Succeed(new OrderConfirmed { OrderId = orderId }))
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("order.rejected", Assert.Single(result.Problems).Code);
        Assert.False(loaded);
        Assert.Equal(0, await fixture.EventStore.GetLastPositionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Validate_IsChainableAndKeepsTheFirstFailure()
    {
        var fixture = new Fixture();
        var secondRan = false;

        var result = await fixture.Store
            .Handle(new ConfirmOrder(Guid.NewGuid()))
            .Validate(_ => Problem.Create("order.first", "First"))
            .Validate(_ =>
            {
                secondRan = true;
                return Problem.Create("order.second", "Second");
            })
            .Decide(_ => Decision.Succeed())
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("order.first", Assert.Single(result.Problems).Code);
        Assert.False(secondRan);
    }

    [Fact]
    public async Task Enrich_FeedsTheDeciderWithoutWideningTheConflictWindow()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();

        var result = await fixture.Store
            .Handle(orderId)
            .Enrich(id => Task.FromResult(new ConfirmOrder(id)))
            .Load(command => Boundary(command.OrderId), new OrderStateEvolver())
            .Decide((command, _) => Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId }))
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, await fixture.EventStore.GetLastPositionAsync(TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------------
    // Unbound commits: no boundary, so the caller must say what to check against.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UnboundCommit_WithAnExplicitBoundary_RejectsAnEventAppendedAfterObservation()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();

        var decision = fixture.Store
            .Handle(new ConfirmOrder(orderId))
            .Decide(command => Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId }));

        await fixture.Append(new OrderReserved { OrderId = orderId });

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            decision.Commit(Boundary(orderId), expectedPosition: 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnboundCommit_WithAValue_RejectsAnEventAppendedAfterObservation()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();

        var decision = fixture.Store
            .Handle(new ConfirmOrder(orderId))
            .Decide(command => Decision<Guid>.Succeed(
                command.OrderId,
                new OrderConfirmed { OrderId = command.OrderId }));

        await fixture.Append(new OrderReserved { OrderId = orderId });

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            decision.Commit(Boundary(orderId), expectedPosition: 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadUnbound_ProducesAPipelineThatStillNeedsAnExplicitBoundary()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();

        var result = await fixture.Store
            .Handle(new ConfirmOrder(orderId))
            .LoadUnbound(_ => Task.FromResult(OrderState.Initial))
            .Decide((command, state) => Decision<int>.Succeed(
                state.EventCount,
                new OrderConfirmed { OrderId = command.OrderId }))
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public async Task LoadUnder_CommitsUnderTheBoundaryTheLoaderReported()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();
        await fixture.Append(new OrderCreated { OrderId = orderId });

        var result = await fixture.Store
            .Handle(new ConfirmOrder(orderId))
            .LoadUnder(async (command, ct) =>
            {
                // A boundary only discoverable during the load: the loader picks it,
                // reads under it, and reports the position it read at.
                var boundary = Boundary(command.OrderId);
                var (state, position) = await fixture.Store.FoldWithPosition(
                    boundary, OrderState.Initial, Fold, ct);
                return (state, boundary, position);
            })
            .Decide((command, state) => Decision<int>.Succeed(
                state.EventCount,
                new OrderConfirmed { OrderId = command.OrderId }))
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
    }

    [Fact]
    public async Task LoadUnder_RejectsAnEventAppendedAfterTheLoaderObservedItsPosition()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();

        var decision = fixture.Store
            .Handle(new ConfirmOrder(orderId))
            .LoadUnder(async (command, ct) =>
            {
                var boundary = Boundary(command.OrderId);
                var (state, position) = await fixture.Store.FoldWithPosition(
                    boundary, OrderState.Initial, Fold, ct);

                // Raced: something lands under the same boundary between the loader's
                // read and the append. The reported position must still be the promise.
                await fixture.Append(new OrderReserved { OrderId = command.OrderId });

                return (state, boundary, position);
            })
            .Decide((command, _) => Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId }));

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            decision.Commit(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CommitUnconditionally_MakesTheAbsenceOfAConflictCheckExplicit()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();
        await fixture.Append(new OrderReserved { OrderId = orderId });

        var result = await fixture.Store
            .Handle(new ConfirmOrder(orderId))
            .Decide(command => Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId }))
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, await fixture.EventStore.GetLastPositionAsync(TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------------
    // Misuse
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DefaultPipeline_FailsWithAMessageThatNamesTheCause()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            default(UnboundDecision).CommitUnconditionally(TestContext.Current.CancellationToken));

        Assert.Contains("store.Handle", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadFromDi_WithoutAServiceProvider_ExplainsHowToFixIt()
    {
        var fixture = new Fixture();
        var orderId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store
                .Handle(new ConfirmOrder(orderId))
                .Load<OrderState>(Boundary(orderId))
                .Decide((command, _) => Decision.Succeed(new OrderConfirmed { OrderId = command.OrderId }))
                .Commit(TestContext.Current.CancellationToken));

        Assert.Contains("Load(boundary, evolver)", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // DI
    // ---------------------------------------------------------------------

    [Fact]
    public async Task WithEventsFrom_UsesTheEventStoreFromEachDependencyScope()
    {
        var services = new ServiceCollection();
        const string moduleKey = "tenant_module";
        services.AddKeyedScoped<IEventStore>(moduleKey, (_, _) => new ScopedEventStore());
        services.AddAlberto(moduleKey, builder => builder
            .WithEventsFrom(typeof(AlbertoStore).Assembly));

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

        await using var firstScope = provider.CreateAsyncScope();
        var firstStore = firstScope.ServiceProvider.GetRequiredKeyedService<AlbertoStore>(moduleKey);
        var firstAdapter = (ScopedEventStore)firstScope.ServiceProvider
            .GetRequiredKeyedService<IEventStore>(moduleKey);

        var firstResult = await firstStore
            .Handle("first")
            .Decide(_ => Decision.Succeed(new ScopeProbe()))
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        await using var secondScope = provider.CreateAsyncScope();
        var secondStore = secondScope.ServiceProvider.GetRequiredKeyedService<AlbertoStore>(moduleKey);
        var secondAdapter = (ScopedEventStore)secondScope.ServiceProvider
            .GetRequiredKeyedService<IEventStore>(moduleKey);

        var secondResult = await secondStore
            .Handle("second")
            .Decide(_ => Decision.Succeed(new ScopeProbe()))
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        Assert.True(firstResult.IsSuccess);
        Assert.True(secondResult.IsSuccess);
        Assert.NotSame(firstStore, secondStore);
        Assert.NotSame(firstAdapter, secondAdapter);
        Assert.Equal(1, firstAdapter.AppendCount);
        Assert.Equal(1, secondAdapter.AppendCount);
    }

    [Fact]
    public async Task LoadFromDi_ResolvesTheRegisteredEvolver()
    {
        var services = new ServiceCollection();
        const string moduleKey = "evolver_module";
        var backend = new InMemoryEventStoreBackend();
        services.AddKeyedScoped<IEventStore>(moduleKey, (_, _) => new EventStore(backend));
        services.AddSingleton<Evolver<OrderState>, OrderStateEvolver>();
        // Register AlbertoStore directly with the explicit serializer rather than via
        // WithEventsFrom, because scanning the test assembly with FromAssembly throws on
        // duplicate [EventType] slugs that exist across different test classes.  The
        // behaviour under test — Load<TState> resolving Evolver<OrderState> from the
        // service provider — requires only that AlbertoStore is keyed and receives the
        // IServiceProvider; it does not depend on how the serializer was built.
        var serializer = CreateSerializer();
        services.AddKeyedScoped(moduleKey, (sp, _) => new AlbertoStore(
            sp.GetRequiredKeyedService<IEventStore>(moduleKey),
            serializer,
            sp));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredKeyedService<AlbertoStore>(moduleKey);
        var orderId = Guid.NewGuid();

        await store
            .Handle(orderId)
            .Decide(id => Decision.Succeed(new OrderCreated { OrderId = id }))
            .CommitUnconditionally(TestContext.Current.CancellationToken);

        var result = await store
            .Handle(new ConfirmOrder(orderId))
            .Load<OrderState>(Boundary(orderId))
            .Decide((command, state) => Decision<int>.Succeed(
                state.EventCount,
                new OrderConfirmed { OrderId = command.OrderId }))
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
    }

    // ---------------------------------------------------------------------

    private static DcbQuery Boundary(Guid orderId) =>
        DcbQuery.ByTags(new EventTag("order", orderId.ToString()));

    private static OrderState Fold(OrderState state, IEvent @event) =>
        state with { EventCount = state.EventCount + 1 };

    private sealed class Fixture
    {
        public Fixture()
        {
            EventStore = new EventStore(new InMemoryEventStoreBackend());
            Serializer = CreateSerializer();
            Store = new AlbertoStore(EventStore, Serializer);
        }

        public EventStore EventStore { get; }
        public EventSerializer Serializer { get; }
        public AlbertoStore Store { get; }

        public Task Append<TEvent>(TEvent @event) where TEvent : IEvent =>
            EventStore.AppendAsync([ToPersist(Serializer, @event)], cancellationToken: CancellationToken.None);
    }

    private static EventToPersist ToPersist<TEvent>(EventSerializer serializer, TEvent @event)
        where TEvent : IEvent
        => new()
        {
            EventType = EventType.FromType(@event.GetType()),
            Tags = serializer.ExtractTags(@event),
            EventData = serializer.Serialize(@event),
            Metadata = new Dictionary<string, string>()
        };

    private static EventSerializer CreateSerializer()
    {
        var registry = new Dictionary<string, Type>
        {
            [EventTypeAttribute.GetEventTypeId(typeof(OrderCreated))] = typeof(OrderCreated),
            [EventTypeAttribute.GetEventTypeId(typeof(OrderReserved))] = typeof(OrderReserved),
            [EventTypeAttribute.GetEventTypeId(typeof(OrderConfirmed))] = typeof(OrderConfirmed),
        };

        return EventSerializer.FromRegistry(registry, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private sealed class ScopedEventStore : IEventStore
    {
        public int AppendCount { get; private set; }

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events,
            DcbQuery? dcbQuery = null,
            long? expectedPosition = null,
            CancellationToken cancellationToken = default)
        {
            AppendCount++;
            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
            DcbQuery query,
            long afterPosition = 0,
            int? limit = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0,
            int? limit = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);
    }
}
