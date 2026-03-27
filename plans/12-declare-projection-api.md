# Plan 12: Replace Projection API with `DeclareProjection`

## Goal
Replace the current `Projection<TState>` base class + `IProject<TState, TEvent>` interface pattern with a single `DeclareProjection` factory that takes a single `evolve` function and `getDocumentId`. This is the **only** projection API going forward — remove the old interface-per-event approach.

## Current Approach (.NET)

```csharp
// Verbose: base class + one interface per event type + reflection dispatch
public class OrderSummaryProjection : Projection<OrderSummary>,
    IProject<OrderSummary, OrderCreated>,
    IProject<OrderSummary, OrderConfirmed>
{
    public string GetDocumentId(OrderCreated e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderCreated e, ProjectionContext ctx)
        => new OrderSummary { OrderId = e.OrderId, Status = "Created" };

    public string GetDocumentId(OrderConfirmed e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderConfirmed e, ProjectionContext ctx)
        => state with { Status = "Confirmed" };
}
```

Problems:
- Requires base class inheritance + N interface implementations
- Reflection-based dispatch (`MethodInfo.Invoke`) on every event
- `GetDocumentId` requires deserialization before knowing if the event is relevant
- No way to skip an event (always returns a result)

## Reference Implementation (TS)

```typescript
const projection = declareProjection<FeatureRM, FeatureEventMap>({
  collectionName: 'features',
  processorId: 'feature-rm-v1',
  canHandle: ['FeatureCreated', 'FeatureUpdated', 'FeatureDeleted'],
  initialState: () => ({ /* defaults */ }),
  getDocumentId: (eventData, envelope) => eventData.featureId ?? null,  // null = skip
  evolve: (state, eventData, context) => {
    // Single function handles all event types via switch/if
    // Return T to set, undefined for unchanged, DELETE to delete
  },
});
```

Key advantages:
- No reflection — direct function dispatch
- Single `evolve` function — can use pattern matching
- `getDocumentId` returning `null` skips the event entirely
- `collectionName` ties projection to storage
- No base class or interface ceremony

## Implementation Plan

### Step 1: Define the new API

```csharp
namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Compiled projection definition — the output of DeclareProjection.
/// </summary>
public sealed class ProjectionDeclaration<TState> where TState : new()
{
    public required string ProcessorId { get; init; }
    public required string CollectionName { get; init; }
    public required IReadOnlySet<string> HandledEventTypes { get; init; }
    public required Func<TState> InitialState { get; init; }

    /// <summary>
    /// Get the document ID for an event. Return null to skip the event.
    /// </summary>
    internal required Func<IEventEnvelope, string?> GetDocumentIdFunc { get; init; }

    /// <summary>
    /// Evolve the state with an event. Return the new state, null for unchanged,
    /// or use ProjectionResults.Delete to delete.
    /// </summary>
    internal required Func<TState, IEventEnvelope, ProjectionContext, ProjectionResult<TState>> EvolveFunc { get; init; }
}
```

### Step 2: Builder / factory method

```csharp
/// <summary>
/// Declare a projection with a single evolve function.
/// This is the recommended way to define projections.
/// </summary>
public static class DeclareProjection
{
    public static ProjectionDeclarationBuilder<TState> For<TState>(string processorId)
        where TState : new()
        => new(processorId);
}

public sealed class ProjectionDeclarationBuilder<TState> where TState : new()
{
    private readonly string _processorId;
    private string _collectionName;
    private readonly HashSet<string> _eventTypes = new();
    private Func<TState> _initialState = () => new TState();
    private Func<IEventEnvelope, string?>? _getDocumentId;
    private Func<TState, IEventEnvelope, ProjectionContext, ProjectionResult<TState>>? _evolve;

    internal ProjectionDeclarationBuilder(string processorId)
    {
        _processorId = processorId;
        _collectionName = processorId;
    }

    public ProjectionDeclarationBuilder<TState> Collection(string name)
    {
        _collectionName = name;
        return this;
    }

    /// <summary>
    /// Declare which event types this projection handles.
    /// </summary>
    public ProjectionDeclarationBuilder<TState> Handles(params string[] eventTypes)
    {
        foreach (var t in eventTypes) _eventTypes.Add(t);
        return this;
    }

    /// <summary>
    /// Declare handled event types from CLR types with [EventType] attribute.
    /// </summary>
    public ProjectionDeclarationBuilder<TState> Handles<TEvent>() where TEvent : IEvent
    {
        _eventTypes.Add(EventTypeAttribute.GetEventTypeId(typeof(TEvent)));
        return this;
    }

    public ProjectionDeclarationBuilder<TState> InitialState(Func<TState> factory)
    {
        _initialState = factory;
        return this;
    }

    /// <summary>
    /// Define how to extract the document ID from an event.
    /// Return null to skip the event (no projection update).
    /// </summary>
    public ProjectionDeclarationBuilder<TState> DocumentId(
        Func<IEventEnvelope, string?> getDocumentId)
    {
        _getDocumentId = getDocumentId;
        return this;
    }

    /// <summary>
    /// Define how to evolve the state when an event is applied.
    /// The event data is available as raw JSON on the envelope.
    /// Return the new state via implicit conversion, ProjectionResults.Delete,
    /// or ProjectionResults.Unchanged.
    /// </summary>
    public ProjectionDeclarationBuilder<TState> Evolve(
        Func<TState, IEventEnvelope, ProjectionContext, ProjectionResult<TState>> evolve)
    {
        _evolve = evolve;
        return this;
    }

    public ProjectionDeclaration<TState> Build()
    {
        ArgumentNullException.ThrowIfNull(_getDocumentId);
        ArgumentNullException.ThrowIfNull(_evolve);
        if (_eventTypes.Count == 0) throw new InvalidOperationException("No event types declared.");

        return new ProjectionDeclaration<TState>
        {
            ProcessorId = _processorId,
            CollectionName = _collectionName,
            HandledEventTypes = _eventTypes,
            InitialState = _initialState,
            GetDocumentIdFunc = _getDocumentId,
            EvolveFunc = _evolve,
        };
    }
}
```

### Step 3: Helper for typed event deserialization

Since the single `evolve` function receives `IEventEnvelope`, provide helpers for typed deserialization:

```csharp
public static class EventEnvelopeExtensions
{
    /// <summary>
    /// Deserialize the event data to a specific type.
    /// </summary>
    public static T ParseEvent<T>(this IEventEnvelope envelope)
        => JsonSerializer.Deserialize<T>(envelope.EventData)
           ?? throw new InvalidOperationException(
               $"Failed to deserialize event '{envelope.EventType.Id}'");
}
```

### Step 4: Usage example

```csharp
var orderSummary = DeclareProjection.For<OrderSummary>("order-summary-v1")
    .Collection("order_summaries")
    .Handles<OrderCreated>()
    .Handles<OrderConfirmed>()
    .Handles<OrderCancelled>()
    .DocumentId(envelope => envelope.ParseEvent<dynamic>()?.orderId?.ToString())
    .Evolve((state, envelope, ctx) => envelope.EventType.Id switch
    {
        "order-created" => state with
        {
            OrderId = envelope.ParseEvent<OrderCreated>().OrderId,
            Status = "Created"
        },
        "order-confirmed" => state with { Status = "Confirmed" },
        "order-cancelled" => state with { Status = "Cancelled" },
        _ => ProjectionResults.Unchanged<OrderSummary>()
    })
    .Build();
```

Or with a helper that deserializes once:

```csharp
.Evolve((state, envelope, ctx) =>
{
    return envelope.EventType.Id switch
    {
        "order-created" => Apply(state, envelope.ParseEvent<OrderCreated>()),
        "order-confirmed" => Apply(state, envelope.ParseEvent<OrderConfirmed>()),
        _ => ProjectionResults.Unchanged<OrderSummary>()
    };

    static OrderSummary Apply(OrderSummary s, OrderCreated e) => s with { /* ... */ };
    static OrderSummary Apply(OrderSummary s, OrderConfirmed e) => s with { /* ... */ };
})
```

### Step 5: Adapt `AsyncProjection` to work with `ProjectionDeclaration`

Create a new internal `DeclaredAsyncProjection<TState>` that wraps a `ProjectionDeclaration<TState>` and implements `IBatchableProcessor` (plan 01). This replaces the reflection-based `ProjectionDispatcher`.

```csharp
internal sealed class DeclaredAsyncProjection<TState> : IBatchableProcessor, IFlushable, IAsyncDisposable
    where TState : new()
{
    private readonly ProjectionDeclaration<TState> _declaration;
    // ... same batching/caching as AsyncProjection but using declaration functions directly
}
```

### Step 6: Update ConsumerBuilder

```csharp
public ConsumerBuilder AddProjection<TState>(
    ProjectionDeclaration<TState> declaration,
    Func<IServiceProvider, Func<string, IStateStore<TState>>> stateStoreFactory)
    where TState : new()
{
    // Register DeclaredAsyncProjection
}
```

### Step 7: Remove old projection API

Remove (or mark `[Obsolete]`) in a single pass:
- `Projection<TState>` base class
- `IProject<TState, TEvent>` interface
- `ProjectionDispatcher<TState>` (reflection-based dispatch)
- `AsyncProjection<TState, TProjection>` (replaced by `DeclaredAsyncProjection`)
- Old `ConsumerBuilder.AddProjection<TState, TProjection>()` overload

Also update `InlineProjection<TState, TProjection>` to use the declaration API.

### Step 8: Update EF integration

`EfConsumerBuilderExtensions.AddEfProjection` needs a new overload accepting `ProjectionDeclaration<TState>`.

### Step 9: Update Orders sample

Convert all projections to the new API.

## Files to Create

- `src/Alberto.Dcb/Subscriptions/ProjectionDeclaration.cs` — declaration type + builder
- `src/Alberto.Dcb/Subscriptions/DeclaredAsyncProjection.cs` — runtime wrapper
- `src/Alberto.Dcb/EventEnvelopeExtensions.cs` — `ParseEvent<T>()` helper

## Files to Modify

- `src/Alberto.Dcb/Subscriptions/Projection.cs` — mark `[Obsolete]`
- `src/Alberto.Dcb/Subscriptions/IProject.cs` — mark `[Obsolete]`
- `src/Alberto.Dcb/Subscriptions/ProjectionDispatcher.cs` — mark `[Obsolete]`
- `src/Alberto.Dcb/Subscriptions/AsyncProjection.cs` — mark `[Obsolete]`
- `src/Alberto.Dcb/Subscriptions/InlineProjection.cs` — update to support declarations
- `src/Alberto.Dcb.EntityFramework/EfConsumerBuilderExtensions.cs` — new overload
- Consumer builder — new `AddProjection` overload
- `apps/Alberto.Orders/` — convert projections to new API
- All test files using old projection API

## Acceptance Criteria

- [ ] `DeclareProjection.For<T>(id).Handles(...).DocumentId(...).Evolve(...).Build()` works
- [ ] No reflection in the projection processing path
- [ ] `getDocumentId` returning `null` skips the event
- [ ] Batch processing works with declared projections (plan 01)
- [ ] Inline projections work with declared projections
- [ ] EF projections work with declared projections
- [ ] Old API is marked `[Obsolete]` with clear migration guidance
- [ ] Orders sample updated to new API
- [ ] All existing tests updated and passing
