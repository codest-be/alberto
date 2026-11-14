using Xunit;

namespace Alberto.Tests;

public class EvolverTests
{
    // Test event
    [EventType("counter-incremented")]
    private record CounterIncremented(int Amount) : IEvent;

    [EventType("counter-reset")]
    private record CounterReset : IEvent;

    private record CounterState(int Count) { public CounterState() : this(0) { } }

    private class CounterEvolver : Evolver<CounterState>,
        IEvolve<CounterState, CounterIncremented>,
        IEvolve<CounterState, CounterReset>
    {
        public CounterState Apply(CounterState state, CounterIncremented e)
            => state with { Count = state.Count + e.Amount };

        public CounterState Apply(CounterState state, CounterReset e)
            => new CounterState();
    }

    [Fact]
    public void Reconstitute_NoEvents_ReturnsDefaultState()
    {
        var evolver = new CounterEvolver();
        var state = evolver.Reconstitute([]);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void Reconstitute_WithEvents_FoldsCorrectly()
    {
        var evolver = new CounterEvolver();
        var envelopes = new[]
        {
            MakeEnvelope("counter-incremented", """{"Amount":5}"""),
            MakeEnvelope("counter-incremented", """{"Amount":3}"""),
            MakeEnvelope("counter-reset", """{}"""),
            MakeEnvelope("counter-incremented", """{"Amount":10}"""),
        };

        var state = evolver.Reconstitute(envelopes);
        Assert.Equal(10, state.Count);
    }

    [Fact]
    public void Reconstitute_UnknownEventType_IgnoresIt()
    {
        var evolver = new CounterEvolver();
        var envelopes = new[]
        {
            MakeEnvelope("unknown-event", """{}"""),
            MakeEnvelope("counter-incremented", """{"Amount":7}"""),
        };

        var state = evolver.Reconstitute(envelopes);
        Assert.Equal(7, state.Count);
    }

    [Fact]
    public void HandledEventTypes_ContainsRegisteredTypes()
    {
        var evolver = new CounterEvolver();
        Assert.Contains("counter-incremented", evolver.HandledEventTypes);
        Assert.Contains("counter-reset", evolver.HandledEventTypes);
        Assert.Equal(2, evolver.HandledEventTypes.Count);
    }

    // ── The typed overload: an event already in hand, no envelope, no JSON ─────────

    [Fact]
    public void Evolve_TypedEvent_DispatchesOnTheRuntimeType()
    {
        var evolver = new CounterEvolver();

        var state = evolver.Evolve(new CounterState(), new CounterIncremented(5));

        Assert.Equal(5, state.Count);
    }

    [Fact]
    public void Evolve_TypedEvents_FoldInOrder()
    {
        var evolver = new CounterEvolver();
        IEvent[] events = [new CounterIncremented(5), new CounterIncremented(3), new CounterReset(), new CounterIncremented(10)];

        var state = events.Aggregate(new CounterState(), evolver.Evolve);

        Assert.Equal(10, state.Count);
    }

    [Fact]
    public void Evolve_TypedEvent_UnhandledTypeLeavesStateUnchanged()
    {
        var evolver = new CounterEvolver();
        var before = new CounterState(7);

        var after = evolver.Evolve(before, new UnrelatedEvent());

        Assert.Equal(7, after.Count);
    }

    [Fact]
    public void Evolve_TypedEvent_AgreesWithTheEnvelopeOverload()
    {
        var evolver = new CounterEvolver();

        var fromEvent = evolver.Evolve(new CounterState(), new CounterIncremented(4));
        var fromEnvelope = evolver.Evolve(new CounterState(), MakeEnvelope("counter-incremented", """{"Amount":4}"""));

        Assert.Equal(fromEnvelope, fromEvent);
    }

    [Fact]
    public void Evolve_TypedEvent_TwoClrTypesSharingAnEventTypeIdFailsLoudly()
    {
        var evolver = new CounterEvolver();

        // Same [EventType] id as CounterIncremented, but not assignable to it. Folding an
        // envelope would upcast; folding an object in hand has nothing to upcast from, so the
        // only honest answer is to say the handler cannot take this.
        var ex = Assert.Throws<InvalidOperationException>(
            () => evolver.Evolve(new CounterState(), new CounterIncrementedImpostor()));

        Assert.Contains("CounterIncrementedImpostor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CounterIncremented", ex.Message, StringComparison.Ordinal);
    }

    [EventType("unrelated-event")]
    private sealed record UnrelatedEvent : IEvent;

    [EventType("counter-incremented")]
    private sealed record CounterIncrementedImpostor : IEvent;

    private static IEventEnvelope MakeEnvelope(string eventType, string data)
        => new TestEnvelope(eventType, data);

    private sealed record TestEnvelope(string TypeId, string Data) : IEventEnvelope
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string? TenantId => null;
        public long GlobalPosition { get; } = 1;
        public EventType EventType => new(TypeId);
        public IReadOnlyCollection<EventTag> Tags { get; } = [];
        public string EventData => Data;
        public IReadOnlyDictionary<string, string> Metadata { get; } = new Dictionary<string, string>();
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    }
}
