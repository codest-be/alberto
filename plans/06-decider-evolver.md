# Plan 06: Decider + Evolver Abstractions

## Goal
Formalize the decide/evolve pattern that's already hand-rolled in the Orders sample. Provide lightweight abstractions that work with the existing event store without requiring inheritance.

## Reference Implementation (TS)

`packages/decider/`:

```typescript
// Decider: command → events
const decider = defineDecider<TCommand, TState, TEvent>(
  (command, state) => [...events]
);

// Evolver: handler map dispatched by event type
const evolver = defineEvolver<TState, TMap>({
  OrderPlaced: (state, data, envelope) => ({ ...state, status: 'placed' }),
  OrderConfirmed: (state, data) => ({ ...state, status: 'confirmed' }),
});

// Reconstitute state from events
const state = reconstitute(events, evolver.evolve);
```

Key insight: The `Evolver` is structurally identical to a `Projection` but for the command side — a handler map dispatched by event type that folds events into state.

## Current Orders Sample (.NET)

```csharp
// Hand-rolled in Alberto.Orders.Core:
public static class OrderDecider
{
    // Separate file per action (7 files), each with:
    // - Interface for required state
    // - Static DecisionResult method
    // - Apply(OrderState, TEvent) method
}

public readonly record struct DecisionResult
{
    // Ok(IEvent) / Fail(string) — hand-rolled
}
```

## Implementation Plan

### Step 1: Add to `Alberto.Dcb` core (no new project needed)

These are small, zero-dependency abstractions that belong in the core library.

### Step 2: `Evolver<TState>` — handler map for state reconstitution

```csharp
namespace Alberto.Dcb;

/// <summary>
/// Handler for evolving state from a specific event type.
/// </summary>
public interface IEvolve<TState, in TEvent> where TEvent : IEvent
{
    TState Apply(TState state, TEvent @event);
}

/// <summary>
/// Folds events into state using a handler map — the command-side equivalent of Projection.
/// Implement IEvolve&lt;TState, TEvent&gt; for each event type.
/// </summary>
public abstract class Evolver<TState> where TState : new()
{
    private readonly EvolverDispatcher<TState> _dispatcher;

    protected Evolver()
    {
        _dispatcher = EvolverDispatcher<TState>.For(this);
    }

    /// <summary>
    /// The event types this evolver handles.
    /// </summary>
    public IReadOnlySet<string> HandledEventTypes => _dispatcher.HandledEventTypes;

    /// <summary>
    /// Apply a single event envelope to the state.
    /// </summary>
    public TState Evolve(TState state, IEventEnvelope envelope)
        => _dispatcher.Evolve(state, envelope);

    /// <summary>
    /// Reconstitute state from a sequence of events.
    /// </summary>
    public TState Reconstitute(IEnumerable<IEventEnvelope> events, TState? initial = default)
        => events.Aggregate(initial ?? new TState(), Evolve);
}
```

`EvolverDispatcher<TState>` mirrors `ProjectionDispatcher<TState>` but returns `TState` directly instead of `ProjectionResult<TState>`.

### Step 3: `DecisionResult<TEvent>` — explicit success/failure

```csharp
namespace Alberto.Dcb;

/// <summary>
/// Result of a decision — either events to append or a failure reason.
/// </summary>
public abstract record DecisionResult<TEvent> where TEvent : IEvent
{
    private DecisionResult() { }

    public sealed record Ok(IReadOnlyList<TEvent> Events) : DecisionResult<TEvent>;
    public sealed record Fail(string Reason) : DecisionResult<TEvent>;

    public static DecisionResult<TEvent> Success(params TEvent[] events)
        => new Ok(events);

    public static DecisionResult<TEvent> Failure(string reason)
        => new Fail(reason);

    public bool IsSuccess => this is Ok;
    public bool IsFailure => this is Fail;

    /// <summary>
    /// Returns events if success, throws InvalidOperationException if failure.
    /// </summary>
    public IReadOnlyList<TEvent> EnsureSuccess()
        => this is Ok ok ? ok.Events
            : throw new InvalidOperationException(((Fail)this).Reason);
}
```

### Step 4: Helper method for the common load-decide-append cycle

```csharp
namespace Alberto.Dcb;

public static class DeciderExtensions
{
    /// <summary>
    /// Loads state from the event store, applies a decision function, and appends resulting events.
    /// Handles the full DCB cycle: stream → reconstitute → decide → append with conflict check.
    /// </summary>
    public static async Task<DecisionResult<TEvent>> DecideAndAppendAsync<TState, TEvent>(
        this IEventStoreBackend eventStore,
        string tenantId,
        DcbQuery boundary,
        Evolver<TState> evolver,
        Func<TState, DecisionResult<TEvent>> decide,
        Func<TEvent, IEventToPersist> toEventToPersist,
        CancellationToken ct = default)
        where TState : new()
        where TEvent : IEvent
    {
        var events = await eventStore.Stream(tenantId, boundary, ct: ct);
        var state = evolver.Reconstitute(events);
        var lastPosition = events.Count > 0 ? events.Max(e => e.GlobalPosition) : 0;

        var result = decide(state);
        if (result is DecisionResult<TEvent>.Fail)
            return result;

        var ok = (DecisionResult<TEvent>.Ok)result;
        var toPersist = ok.Events.Select(toEventToPersist);

        await eventStore.Append(tenantId, toPersist, boundary, lastPosition, ct);
        return result;
    }
}
```

### Step 5: Update Orders sample to use new abstractions

```csharp
// Before: hand-rolled DecisionResult, separate Apply methods in partial classes
// After:
public class OrderEvolver : Evolver<OrderState>,
    IEvolve<OrderState, OrderCreated>,
    IEvolve<OrderState, OrderConfirmed>,
    // ...
{
    public OrderState Apply(OrderState state, OrderCreated e)
        => state with { Status = OrderStatus.Draft, /* ... */ };

    public OrderState Apply(OrderState state, OrderConfirmed e)
        => state with { Status = OrderStatus.Confirmed };
}
```

## Files to Create

- `src/Alberto.Dcb/IEvolve.cs` — evolve handler interface
- `src/Alberto.Dcb/Evolver.cs` — base class with dispatcher
- `src/Alberto.Dcb/EvolverDispatcher.cs` — reflection-based dispatch (mirrors ProjectionDispatcher)
- `src/Alberto.Dcb/DecisionResult.cs` — success/failure result type
- `src/Alberto.Dcb/DeciderExtensions.cs` — load-decide-append helper
- `tests/Alberto.Dcb.Tests/EvolverTests.cs`
- `tests/Alberto.Dcb.Tests/DecisionResultTests.cs`

## Files to Modify (optional — update sample)

- `apps/Alberto.Orders/Alberto.Orders.Core/` — refactor to use Evolver
- `apps/Alberto.Orders/Alberto.Orders.Api/OrderMutations.cs` — use DecideAndAppendAsync

## Acceptance Criteria

- [ ] `Evolver<TState>` reconstitutes state from events via handler interfaces
- [ ] `DecisionResult<TEvent>` represents success/failure with events
- [ ] `DecideAndAppendAsync` handles the full load-decide-append cycle
- [ ] Orders sample updated to use new abstractions (optional, can be separate PR)
- [ ] Unit tests for Evolver fold, DecisionResult, and DecideAndAppendAsync
