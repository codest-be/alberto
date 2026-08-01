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
