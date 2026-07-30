# Per-slice state — detailed recipe

## 0. The safety net, before anything moves

Snapshot the module's external contract in a test that fails on any diff, with a documented escape
hatch for re-recording an intentional change:

```csharp
[Fact]
public async Task Schema_matches_the_snapshot()
{
    var schema = (await BuildSchemaAsync()).Print();

    if (Environment.GetEnvironmentVariable("REWRITE_SNAPSHOT") == "1")
        await File.WriteAllTextAsync(SnapshotPath, Normalise(schema));

    Normalise(schema).Should().Be(await File.ReadAllTextAsync(SnapshotPath));
}
```

**Normalise only what the refactor legitimately reorders.** Splitting one class of resolvers into
twelve reshuffles the order root fields are discovered in, so sort the fields of `Query` and
`Mutation` by name — and leave *everything else* byte-exact, including descriptions. That
combination is what makes the snapshot useful: it tolerates the move and still catches a reworded
`[GraphQLDescription]`, a changed default, a dropped nullability.

When it fails, diagnose by re-recording and reading the diff rather than by staring at the assert:

```bash
REWRITE_SNAPSHOT=1 dotnet test <test-project> && git diff -- <snapshot-path>
```

Then `git checkout --` the snapshot and fix the code. Never commit a re-recorded snapshot during a
mechanical refactor: a snapshot you rewrote is a test you deleted.

## 1. The target layout

```
Module/
  Contracts/            # events, status enums, problem codes, tag keys — the shared vocabulary
  Features/
    ShipOrder/
      ShipOrder.cs      # input, state, evolver, boundary, decision, transport
    CancelOrder/
      CancelOrder.cs
    OrderSummaries/     # read slice: projection + read model + every query over it
  Platform/             # DI registration, DbContext, migrations — composition, not domain
```

One file per slice is deliberate. It is the unit a reviewer reads, and being able to hold a whole
behaviour in view is the point of the exercise.

## 2. A converted write slice

```csharp
/// <summary>Input for shipping an order.</summary>
public sealed record ShipOrderInput(Guid OrderId, string TrackingNumber, string Carrier);

/// <summary>
/// Two properties. Shipping never sees LineItems, Notes, CustomerId or the timestamps of the
/// other transitions.
/// </summary>
public sealed record ShipOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeShipped => Status == OrderStatus.Confirmed;
}

/// <remarks>
/// OrderItemAdded and OrderItemRemoved are ignored: they cannot change whether an order is
/// shippable. OrderDelivered can't either — but the refusal message names the status, so
/// leaving it out would tell a client a delivered order "cannot be shipped in Shipped status".
/// </remarks>
public sealed class ShipOrderEvolver : Evolver<ShipOrderState>,
    IEvolve<ShipOrderState, OrderCreated>,
    IEvolve<ShipOrderState, OrderConfirmed>,
    IEvolve<ShipOrderState, OrderShipped>,
    IEvolve<ShipOrderState, OrderDelivered>,
    IEvolve<ShipOrderState, OrderCancelled>
{
    public ShipOrderState Apply(ShipOrderState s, OrderCreated e) =>
        s with { OrderId = e.OrderId, Status = OrderStatus.Draft };

    public ShipOrderState Apply(ShipOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    // …one per event, each naming only the properties this slice holds
}

public static class ShipOrderDecider
{
    /// <summary>
    /// Whole-order boundary: any concurrent order event conflicts with a ship. Narrowing the
    /// type axis would let non-overlapping slices commit together, but every event that could
    /// invalidate this decision would have to be listed here or the decision silently loses
    /// updates. That argument is per slice; this one has not been made.
    /// </summary>
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(
        ShipOrderState state, string trackingNumber, string carrier, DateTimeOffset shippedAt)
    {
        if (!state.Exists) return Decision.Fail(OrderProblems.NotFound());
        if (!state.CanBeShipped)
            return Decision.Fail(OrderProblems.InvalidStatus("shipped", state.Status));
        if (string.IsNullOrWhiteSpace(trackingNumber))
            return Decision.Fail(OrderProblems.TrackingNumberRequired());

        return Decision.Succeed(new OrderShipped(state.OrderId, trackingNumber, carrier, shippedAt));
    }
}

public static class ShipOrderMutation
{
    [Mutation]
    [GraphQLDescription("Ships a confirmed order with tracking information.")]
    public static async Task<MutationResult> ShipOrder(
        ShipOrderInput input, [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider, CancellationToken ct)
    { /* Handle → Load → Decide → Commit, exactly as before the move */ }
}
```

The comments carrying the *why* — what this slice ignores, and why the boundary was not narrowed —
are load-bearing. They are the argument a future reader would otherwise have to reconstruct.

## 3. Ordering the conversions

Convert the **narrowest slice first**. It is the smallest diff, it proves the snapshot test works,
and it establishes the file shape the rest copy.

Two slices can want the same-named property with different meanings. Convert them **adjacently**
and name the difference in each one's doc comment:

```csharp
// CapturePaymentState — the amount the payment was initiated for: both the default when the
// caller omits one and the ceiling when they supply one.
public decimal Amount { get; init; }

// RefundPaymentState — a refund is bounded by what was captured, not by what was initiated.
public decimal? CapturedAmount { get; init; }
```

On a shared record these are one field, and whichever slice loses the argument gets the wrong
ceiling. This pair is the strongest argument for the whole exercise — find it in your own domain and
put it in the commit message.

## 4. Cutting over without breaking the unconverted slices

The shared record cannot be deleted until the last slice is converted, so each intermediate commit
must leave the old machinery compiling. Per slice, delete only:

- the narrowing interface (`IShipOrderState`), and
- the old `static Decision Ship(...)` method.

**Keep the old `Apply(SharedState, OrderShipped)` fragment** — the shared evolver still needs it for
the slices not yet converted. Remove the interface from the shared record's implements list; when the
list empties, the declaration reduces to a bare `public sealed record OrderState` and the final
sweep deletes it, its evolver, its decider and the `Apply` fragments together.

Verify the sweep found everything, with word boundaries so per-slice names don't mask the bare ones:

```bash
grep -rnE "\b(OrderState|OrderEvolver|OrderDecider)\b" --include="*.cs" apps | grep -v "/obj/"
```

Expect no output.

## 5. Read slices

Group by **read model**, not by field: a projection and every query over it change together, so
`orders` and `recentOrders` over one projection are one slice.

Two rules earn their own commits:

**A projection builds the store it writes.** Move the state-store construction out of the module
registration and into the projection, then have queries resolve *that* factory from DI rather than
constructing a store:

```csharp
public static Func<string?, IStateStore<OrdersOverview>> StateStore(ProjectionStoreContext ctx)
{
    var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
    return tenantId => new PostgresStateStore<OrdersOverview>(
        dataSource, nameof(OrdersOverviewProjection), "orders",
        rebuildVersion: ctx.RebuildVersion,
        tenantId: TenantScope.CrossTenantFor(tenantId));
}
```

Writer and reader then cannot disagree about schema, projection name, rebuild version or tenancy —
the only thing a query decides is which tenant to read. A reader that builds its own store is how a
field queries a `(tenant_id, projection_type, …)` key while the writer stores rows under
`(projection_type, …)` and returns nothing for months. Make the document id a public constant on the
projection too, so writer selectors and reader keys are the same symbol.

**Replace hand-rolled folds with a real evolver.** A read field that switches over event-type ids,
deserializes each payload itself, then switches again to pick an `Apply` overload has three lists
that must agree; a new event is dropped by whichever was not updated. An evolver derives all three
from its `IEvolve<,>` interfaces. Pin it:

```csharp
new GetPaymentEvolver().HandledEventTypes.Should().BeEquivalentTo(
    ["payment-initiated", "payment-authorized", "payment-captured",
     "payment-failed", "payment-refunded"]);
```

**A wide read state is still one slice's state.** `GetOrderState` may have twelve properties because
the field exposes the whole order. That it resembles the record you just deleted is the point: one
slice's state that happens to be wide, folded by one slice's evolver, with no decision depending on
it. A *write* slice reaching for it is the regression to watch for.

Output types (`Order`, `Payment`) may be shared between two read slices. That is an output contract,
not shared state — nothing folds events into them. Say so in a comment where they are declared.

## 6. Tests per slice

Unit-test the decision function and the evolver directly; they are pure. If the framework's
`Reconstitute` takes envelopes rather than events, chain `Apply` calls instead of constructing
envelopes:

```csharp
var evolver = new ShipOrderEvolver();
var state = evolver.Apply(new ShipOrderState(), new OrderCreated(orderId, …));
state = evolver.Apply(state, new OrderDelivered(orderId, deliveredAt));

var decision = ShipOrderDecider.Decide(state, "TRACK-1", "DHL", now);

decision.IsError.Should().BeTrue();
decision.Problems[0].Message.Should().Contain("Delivered");   // the trap, pinned
```

Two assertions per slice are worth writing every time: **the refusal message names the real status**
(the error-message trap), and **`HandledEventTypes` is exactly what you intended to fold** (so a new
domain event forces a decision in each slice rather than being silently ignored).

## 7. Finishing

- Delete the shared record, evolver, decider and `Apply` fragments in one commit whose message says
  *why*: it is the deletion that makes the slicing true.
- Update every doc that names a moved type or path, and any XML `<see cref>` pointing at one.
- Add an architecture doc covering: what a slice owns, why state is not shared (with the
  two-meanings-one-field pair), what stays global and why each one, the duplication rule, boundaries,
  and how read slices are grouped.
