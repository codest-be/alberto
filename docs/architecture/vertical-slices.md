# Vertical slices in the examples

`apps/Alberto.Orders` and `apps/Alberto.Payments` are sliced by behaviour, not by layer. There is
no `Core` project of domain models and no `Infrastructure` project of data access: each module is
one assembly with three top-level folders, and the one that matters is `Features/`, holding one
folder per slice.

Slices share the event log and nothing else.

## What a slice owns

`Features/ShipOrder/ShipOrder.cs` is the reference. One file, six things:

| | |
|---|---|
| `ShipOrderInput` | the GraphQL input type |
| `ShipOrderState` | this slice's state, two properties |
| `ShipOrderEvolver` | which events it folds, and how |
| `ShipOrderDecider.Boundary` | the DCB consistency boundary |
| `ShipOrderDecider.Decide` | the decision, as a pure function |
| `ShipOrderMutation.ShipOrder` | the GraphQL field that wires them together |

```csharp
public sealed record ShipOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeShipped => Status == OrderStatus.Confirmed;
}
```

Everything a reviewer needs in order to judge "does shipping work?" is in that file, and nothing
else in the module has to be read to be sure nothing else was affected.

## Why state is not shared

`ShipOrderState` has two properties. The record it replaced had twelve, and every action carried
all of them: line items, notes, customer id, the timestamp of every other transition. A field
added for one action was visible to all of them, and a decision could come to depend on data its
own behaviour had no business seeing.

The clearest case is a pair of payment slices that a single record cannot serve honestly:

- `CapturePaymentState.Amount` is the amount the payment was *initiated* for. Capture uses it as
  both the default when the caller omits an amount and the ceiling when they supply one.
- `RefundPaymentState.CapturedAmount` is the amount that was actually *captured*. A refund is
  bounded by that, not by what was initiated.

Both are "the payment's amount". On a shared record they are one field, and whichever slice loses
the argument gets the wrong ceiling: a refund capped at the initiated amount will hand back money
that was never taken. As two slices they are two fields, each folded from the event that defines
it, and neither slice has to know the other exists.

The narrowing also decides what an evolver may ignore. `EvolverDispatcher` silently skips event
types a slice does not handle, so `ShipOrderEvolver` folds five of the seven order events and says
in a comment why it folds the two that cannot change shippability: the refusal message names the
current status, so a slice that ignored `OrderDelivered` would tell a client that a delivered order
"cannot be shipped in Shipped status".

## What is global, and why

Four things are shared on purpose, and they live in `Contracts/` so they cannot be mistaken for
domain code that merely happens to be shared:

- **Events** (`Contracts/OrderEvents.cs`). The log is the one thing every slice reads. This is
  what makes slices independent rather than isolated: `OrderCreated` is folded by five different
  slices, each projecting a different part of it.
- **Status enums** (`Contracts/OrderStatus.cs`): persisted by name in read models and exposed in
  the GraphQL schema. Two copies would be two schema enums.
- **Problem codes** (`Contracts/OrderProblems.cs`): a client contract. A slice inventing its own
  code for "not found" changes the API.
- **Tag keys** (`Contracts/Tags.cs`): consistency boundaries are built from them, so two slices
  spelling a tag differently would silently stop conflicting with each other.

`Platform/` is the second exception: DI registration, the `DbContext`, EF migrations. Composition,
not domain.

## The duplication rule

Five slices fold `OrderCreated` and each writes its own `Apply`. That duplication is the pattern
working, not a smell to be factored out. A shared `ApplyCreated` helper or a base state record
reintroduces exactly what slicing removed: a place where a change made for one behaviour reaches
another.

**If two slices want the same helper, they get two copies.** The only shared code is in
`Contracts/` and `Platform/`, and adding to either is a deliberate decision about the module's
public contract.

Module-level constants are the narrow exception: `OrderSlices.ConflictRetries` and
`PaymentSlices.ConflictRetries` are one number each, a policy rather than domain logic, and every
retrying slice cites the same one.

## Boundaries

Every write slice uses the whole-aggregate boundary:

```csharp
public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);
```

Any concurrent order event conflicts with a ship. Narrowing the type axis (listing only the event
types that could invalidate *this* decision) would let non-overlapping slices commit together, but
every such event has to be enumerated or the decision silently loses updates. That argument is
per slice, needs a per-slice justification, and belongs on one line in the slice that makes it. So
far none has.

## Read slices

Read slices are grouped by **read model**, not by field, because a projection and every query over
it change together:

| Slice | Contents |
|---|---|
| `Features/OrderSummaries/` | the EF projection, the `Order`/`OrderItem`/`OrdersConnection` types, `orders` and `recentOrders` |
| `Features/OrdersOverview/` | the aggregate projection, its read model, its state store, `ordersOverview` |
| `Features/GetOrder/` | `GetOrderState` + `GetOrderEvolver`, folded from the log on every call |
| `Features/PaymentSummaries/` | the per-payment projection, the `Payment` type, `recentPayments` |
| `Features/PaymentsOverview/` | the aggregate projection, its read model, its state store, `paymentsOverview` |
| `Features/GetPayment/` | `GetPaymentState` + `GetPaymentEvolver` |

Two consequences worth naming:

**A projection builds the store it writes.** `OrdersOverviewProjection.StateStore` and
`PaymentsOverviewProjection.StateStore` are `Func<ProjectionStoreContext, Func<string?, IStateStore<T>>>`
handed to `AddProjection`, which also registers them under the DI key `{moduleKey}:{processorId}`.
Queries resolve *that* factory rather than constructing a store, so writer and reader cannot
disagree about schema, projection name, rebuild version or tenancy. The only thing a query decides
is which tenant to read. A reader that built its own store is how `ordersOverview` once queried a
`(tenant_id, projection_type, …)` key while the writer stored rows under `(projection_type, …)`,
and returned nothing for as long as it did.

**A read state is still one slice's state.** `GetOrderState` has twelve properties, because
`getOrder` exposes the whole order. That it resembles the shared record this refactor deleted is
the point: it is one slice's state that happens to be wide, folded by one slice's evolver, and no
decision depends on it. A write slice reaching for it would be reintroducing the shared record
under a new name.

The two `Order` and `Payment` GraphQL records are shared between two read slices each: an *output
contract*, which is a different thing from shared state. Nothing folds events into them.
