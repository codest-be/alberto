using Xunit;

namespace Alberto.Tests;

/// <summary>
/// Verifies correctness of the compiled-delegate dispatch introduced to replace
/// MethodInfo.Invoke in EvolverDispatcher&lt;TState&gt;.Handler.Evolve.
///
/// Four scenarios are covered:
///   1. record struct TState — value types must not be boxed and must fold correctly.
///   2. Upcaster type-mismatch guard — original exception message must survive; the
///      compiled Expression.Convert must never fire before the IsInstanceOfType check.
///   3. Multi-event dispatch — an evolver implementing several IEvolve&lt;,&gt; routes
///      each event to the correct handler.
///   4. Unhandled event types — state must be returned unchanged.
/// </summary>
public sealed class EvolverDispatchPerformanceTests
{
    // -----------------------------------------------------------------------
    // Shared envelope helper (mirrors the pattern used in EvolverTests.cs)
    // -----------------------------------------------------------------------

    private static IEventEnvelope MakeEnvelope(string eventType, string data, int version = 1)
        => new TestEnvelope(eventType, data, version);

    private sealed record TestEnvelope(string TypeId, string Data, int Version) : IEventEnvelope
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string? TenantId => null;
        public long GlobalPosition { get; } = 1;
        public EventType EventType => new(TypeId, Version);
        public IReadOnlyCollection<EventTag> Tags { get; } = [];
        public string EventData => Data;
        public IReadOnlyDictionary<string, string> Metadata { get; } = new Dictionary<string, string>();
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    }

    // -----------------------------------------------------------------------
    // 1. record struct TState — value types must fold without boxing
    // -----------------------------------------------------------------------

    [EventType("perf-item-added")]
    private record ItemAdded(string Name) : IEvent;

    [EventType("perf-item-removed")]
    private record ItemRemoved(string Name) : IEvent;

    /// <summary>
    /// A record struct state — ensures the delegate path does not box TState.
    /// </summary>
    private record struct ItemBagState(int Count, string LastName)
    {
        public ItemBagState() : this(0, "") { }
    }

    private sealed class ItemBagEvolver : Evolver<ItemBagState>,
        IEvolve<ItemBagState, ItemAdded>,
        IEvolve<ItemBagState, ItemRemoved>
    {
        public ItemBagState Apply(ItemBagState state, ItemAdded e)
            => state with { Count = state.Count + 1, LastName = e.Name };

        public ItemBagState Apply(ItemBagState state, ItemRemoved e)
            => state with { Count = state.Count - 1, LastName = e.Name };
    }

    [Fact]
    public void RecordStruct_FoldsCorrectlyThroughCompiledDelegate()
    {
        var evolver = new ItemBagEvolver();

        var envelopes = new[]
        {
            MakeEnvelope("perf-item-added",   """{"Name":"apple"}"""),
            MakeEnvelope("perf-item-added",   """{"Name":"banana"}"""),
            MakeEnvelope("perf-item-removed", """{"Name":"apple"}"""),
        };

        var state = evolver.Reconstitute(envelopes);

        Assert.Equal(1, state.Count);
        Assert.Equal("apple", state.LastName);
    }

    // -----------------------------------------------------------------------
    // 2. Upcaster type-mismatch guard fires BEFORE Expression.Convert
    //    (would throw InvalidCastException if it reached the delegate)
    // -----------------------------------------------------------------------

    [EventType("perf-order-placed")]
    private record PerfOrderPlaced(Guid OrderId) : IEvent;

    // A different CLR type that the upcaster chain produces but the handler does not expect.
    private record PerfWrongEvent(string Tag) : IEvent;

    private record PerfOrderState(Guid? LastOrderId)
    {
        public PerfOrderState() : this((Guid?)null) { }
    }

    private sealed class PerfOrderEvolver : Evolver<PerfOrderState>,
        IEvolve<PerfOrderState, PerfOrderPlaced>
    {
        public PerfOrderState Apply(PerfOrderState state, PerfOrderPlaced e)
            => state with { LastOrderId = e.OrderId };
    }

    [Fact]
    public void TypeMismatchGuard_ThrowsOriginalMessage_NotInvalidCastException()
    {
        var evolver = new PerfOrderEvolver();
        var envelope = MakeEnvelope("perf-order-placed", """{"OrderId":"00000000-0000-0000-0000-000000000001"}""");

        // Simulate an upcaster chain that returns the wrong type.
        Func<IEventEnvelope, object> badDeserializer = _ => new PerfWrongEvent("oops");

        var act = () => evolver.Reconstitute([envelope], initial: null, badDeserializer);

        var exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Contains("Upcaster for 'perf-order-placed'", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PerfWrongEvent", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PerfOrderPlaced", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IEvolve<TState, TEvent>", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // 3. Multi-event dispatch — each event type routes to the right handler
    // -----------------------------------------------------------------------

    [EventType("perf-counter-incremented")]
    private record PerfCounterIncremented(int Amount) : IEvent;

    [EventType("perf-counter-reset")]
    private record PerfCounterReset : IEvent;

    [EventType("perf-counter-named")]
    private record PerfCounterNamed(string Name) : IEvent;

    private record PerfCounterState(int Count, string Label)
    {
        public PerfCounterState() : this(0, "") { }
    }

    private sealed class PerfCounterEvolver : Evolver<PerfCounterState>,
        IEvolve<PerfCounterState, PerfCounterIncremented>,
        IEvolve<PerfCounterState, PerfCounterReset>,
        IEvolve<PerfCounterState, PerfCounterNamed>
    {
        public PerfCounterState Apply(PerfCounterState state, PerfCounterIncremented e)
            => state with { Count = state.Count + e.Amount };

        public PerfCounterState Apply(PerfCounterState state, PerfCounterReset e)
            => state with { Count = 0 };

        public PerfCounterState Apply(PerfCounterState state, PerfCounterNamed e)
            => state with { Label = e.Name };
    }

    [Fact]
    public void MultipleEventTypes_DispatchToCorrectHandlers()
    {
        var evolver = new PerfCounterEvolver();

        var envelopes = new[]
        {
            MakeEnvelope("perf-counter-named",       """{"Name":"hits"}"""),
            MakeEnvelope("perf-counter-incremented", """{"Amount":10}"""),
            MakeEnvelope("perf-counter-incremented", """{"Amount":5}"""),
            MakeEnvelope("perf-counter-reset",       """{}"""),
            MakeEnvelope("perf-counter-incremented", """{"Amount":3}"""),
        };

        var state = evolver.Reconstitute(envelopes);

        Assert.Equal(3, state.Count);
        Assert.Equal("hits", state.Label);
    }

    // -----------------------------------------------------------------------
    // 4. Unhandled event types leave state unchanged
    // -----------------------------------------------------------------------

    [Fact]
    public void UnhandledEventType_LeavesStateUnchanged()
    {
        var evolver = new PerfCounterEvolver();

        var envelopes = new[]
        {
            MakeEnvelope("perf-counter-incremented", """{"Amount":7}"""),
            MakeEnvelope("completely-unknown-event", """{"Anything":"here"}"""),
            MakeEnvelope("also-unknown",             """{}"""),
        };

        var state = evolver.Reconstitute(envelopes);

        // Only the one known event should have contributed.
        Assert.Equal(7, state.Count);
        Assert.Equal("", state.Label);
    }
}
