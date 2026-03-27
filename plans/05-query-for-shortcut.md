# Plan 05: `DcbQuery.For()` Shortcut

## Goal
Add a `DcbQuery.For(concept, id)` factory method for the most common query pattern — a single exact tag match. This covers ~90% of use cases and dramatically improves DX.

## Current Usage (.NET)

```csharp
// Current: verbose
var query = DcbQuery.Empty.WithTag("order", orderId.ToString());
var query = DcbQuery.ByTags(new EventTag("order", orderId.ToString()));

// Also common: tag + types
var query = DcbQuery.Empty
    .WithTag("order", orderId.ToString())
    .WithTypes("order-placed", "order-cancelled");
```

## Reference (TS)

```typescript
// Concise
const q = query.for('order', orderId);
const q = query.for('order', orderId).withTypes('OrderPlaced');
```

## Implementation

Add to `DcbQuery.cs`:

```csharp
/// <summary>
/// Creates a query for a single concept:id tag — the most common pattern.
/// Equivalent to DcbQuery.ByTags(new EventTag(concept, id)).
/// </summary>
/// <example>
/// <code>
/// var q = DcbQuery.For("order", orderId);
/// var q = DcbQuery.For("order", orderId).WithTypes("order-placed");
/// </code>
/// </example>
public static DcbQuery For(string concept, string id)
    => ByTags(new EventTag(concept, id));

/// <summary>
/// Creates a query for a single concept:id tag using a non-string ID.
/// Calls ToString() on the id parameter.
/// </summary>
public static DcbQuery For(string concept, Guid id)
    => ByTags(new EventTag(concept, id.ToString()));

/// <summary>
/// Creates a query for a single concept:id tag using a non-string ID.
/// Calls ToString() on the id parameter.
/// </summary>
public static DcbQuery For(string concept, int id)
    => ByTags(new EventTag(concept, id.ToString()));

/// <summary>
/// Creates a query for a single concept:id tag using a non-string ID.
/// Calls ToString() on the id parameter.
/// </summary>
public static DcbQuery For(string concept, long id)
    => ByTags(new EventTag(concept, id.ToString()));
```

Also add a generic overload for any type:

```csharp
/// <summary>
/// Creates a query for a single concept:id tag. Calls ToString() on the id.
/// </summary>
public static DcbQuery For<TId>(string concept, TId id) where TId : notnull
    => ByTags(new EventTag(concept, id.ToString()!));
```

## Files to Modify

- `src/Alberto.Dcb/DcbQuery.cs` — add `For()` factory methods
- `tests/Alberto.Dcb.Tests/DcbQueryTests.cs` — add tests for `For()`
- `apps/Alberto.Orders/Alberto.Orders.Core/Tags.cs` — consider adding a `BoundaryFor` that uses `DcbQuery.For`

## Update Orders Sample

In `Alberto.Orders.Core`, the current boundary helper could become:

```csharp
// Before
public static DcbQuery BoundaryFor(Guid orderId)
    => DcbQuery.Empty.WithTag(Tags.Order, orderId.ToString());

// After
public static DcbQuery BoundaryFor(Guid orderId)
    => DcbQuery.For(Tags.Order, orderId);
```

## Acceptance Criteria

- [ ] `DcbQuery.For("order", "123")` creates correct single-tag query
- [ ] `DcbQuery.For("order", someGuid)` works with Guid IDs
- [ ] Chaining works: `DcbQuery.For("order", id).WithTypes("order-placed")`
- [ ] XML doc comments with examples
- [ ] Tests for all overloads
