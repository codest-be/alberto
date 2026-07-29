# Vertical slices with state per slice

Date: 2026-07-29

## Problem

The Orders and Payments examples look sliced and are not. Each action has its own
file under `Order/Actions/` and its own state interface — `IShipOrderState`,
`IAddItemState`, and five more — but every one of those interfaces is implemented by
a single `OrderState` record, folded by a single `OrderEvolver`, mutated by `Apply`
methods hanging off a single `partial class OrderDecider`. The interfaces narrow the
*view* onto a shared object; they do not remove the sharing. Add a field for one
action and every action's state grows.

That shows in the code. `IAddItemState` declares `LineItems`, but `AddItem` never
reads it — only the shared `Apply` does, and it is on the interface because the
shared record has it. Ship carries `LineItems`, `Notes`, `CustomerId` and every
timestamp through a decision that reads two properties.

The layering compounds it. `Alberto.Orders.Core` holds the actions,
`Alberto.Orders.Infrastructure` the projections, `Alberto.Orders.Api` the GraphQL
mutations, so a single behaviour is spread across three projects sliced by
technical layer — the opposite axis. Payments has no API project at all; its
mutations live in the Orders API.

## Goal

An example where the only thing shared between slices is the event log. State,
evolver, decision and transport all belong to one action and live in one file. What
the example demonstrates should be true of the example.

Nothing in `src/Alberto.Dcb*` changes. The framework already supports this:
`CommandPipeline.Load<TState>(boundary, evolver)` takes the evolver per call site,
the DI overload resolves on the closed generic `Evolver<TState>` so N slice states
give N registrations, and `EvolverDispatcher` leaves unhandled event types
untouched, so a slice evolver folding three of seven events works over any boundary.

## Design

### Project layout

Each module becomes one class library holding every slice, with a thin host beside it.

```
apps/Alberto.Orders/
  Alberto.Orders/             Core + Infrastructure merged; all slices
  Alberto.Orders.Api/         Program.cs and ServiceDefaults wiring
  Alberto.Orders.Migrations/  unchanged; references the module library
apps/Alberto.Payments/
  Alberto.Payments/           Core + Infrastructure merged; all slices
```

The module libraries take `HotChocolate.Types` and `HotChocolate.Types.Analyzers`
so `[Mutation]` and `[Query]` can sit in the slice file. The API calls the
generated `AddOrdersTypes()` and `AddPaymentsTypes()`. Payments gains ownership of
its own mutations, which today sit in `Alberto.Orders.Api/GraphQL/Mutations/PaymentMutations.cs`.

The Migrations worker keeps referencing a class library rather than a web project,
which is why the host stays separate instead of everything collapsing into the API.

### Inside a module

```
Alberto.Orders/
  Contracts/            OrderEvents.cs, OrderStatus.cs, OrderProblems.cs, Tags.cs
  Features/
    CreateOrder/CreateOrder.cs
    ShipOrder/ShipOrder.cs
    ...
    GetOrder/GetOrder.cs
    OrdersOverview/OrdersOverview.cs
  Platform/             OrdersModule.cs, OrdersDbContext.cs, Migrations/
```

One file per slice, holding the input type, the state record, the evolver, the
decision function, the boundary and the GraphQL operation. Nothing outside a slice
folder references anything inside it. `Contracts/` and `Platform/` are the two
deliberate exceptions, and both are named so they cannot be mistaken for domain
code that happens to be shared.

`Platform/` is composition, not behaviour: DI registration, the EF `DbContext`, EF
migrations. Slices declare what they need; `OrdersModule` assembles it.

### What stays global

Events, and only events, plus the vocabulary the events are written in:

- `OrderEvents.cs` — the events and `OrderLineItem`, which is an `OrderCreated` payload.
- `OrderStatus` — a behaviourless enum used by problem messages and read models.
- `OrderProblems` — error codes are client contract, versioned like the schema.
- `Tags` — the tag keys boundaries are built from.

Everything else is per slice. This is the line to defend in review: a helper that
starts in `Contracts/` because two slices want it is how the shared state object
grows back.

### A slice

```csharp
// Features/ShipOrder/ShipOrder.cs
public sealed record ShipOrderInput(Guid OrderId, string TrackingNumber, string Carrier);

public sealed record ShipOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeShipped => Status == OrderStatus.Confirmed;
}

public sealed class ShipOrderEvolver : Evolver<ShipOrderState>,
    IEvolve<ShipOrderState, OrderCreated>,
    IEvolve<ShipOrderState, OrderConfirmed>,
    IEvolve<ShipOrderState, OrderShipped>,
    IEvolve<ShipOrderState, OrderCancelled>
{
    public ShipOrderState Apply(ShipOrderState s, OrderCreated e) =>
        s with { OrderId = e.OrderId, Status = OrderStatus.Draft };
    public ShipOrderState Apply(ShipOrderState s, OrderConfirmed e) => s with { Status = OrderStatus.Confirmed };
    public ShipOrderState Apply(ShipOrderState s, OrderShipped e) => s with { Status = OrderStatus.Shipped };
    public ShipOrderState Apply(ShipOrderState s, OrderCancelled e) => s with { Status = OrderStatus.Cancelled };
}

public static class ShipOrder
{
    // Whole-order boundary: any concurrent order event conflicts with a ship.
    private static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(ShipOrderState state, ShipOrderInput input, DateTimeOffset shippedAt) { ... }

    [Mutation]
    public static async Task<MutationResult> ShipOrder(...) { ... }
}
```

`ShipOrderState` holds two properties. It never sees `LineItems`, `Notes`,
`CustomerId` or the timestamps of the other transitions, and its evolver ignores
`OrderItemAdded` and `OrderItemRemoved` entirely.

`OrderCreated` is folded by five slices, each projecting a different part of it.
That duplication is the pattern working. Factoring it back out — a shared
`ApplyCreated`, a base state record — reintroduces exactly what this removes.

### Slice inventory

Write slices, twelve: CreateOrder, AddOrderItem, RemoveOrderItem, ConfirmOrder,
ShipOrder, DeliverOrder, CancelOrder; InitiatePayment, AuthorizePayment,
CapturePayment, FailPayment, RefundPayment.

Read slices, seven: GetOrder, GetOrders, GetRecentOrders, OrdersOverview;
GetPayment, GetRecentPayments, PaymentsOverview.

`GetOrder` and `GetPayment` fold the log on the read path through the same shared
evolver the mutations use, so they get their own read state too. A projection and
the query that serves it belong to the same slice: `OrdersOverview.cs` holds the
`ProjectionDeclaration`, the `OrdersOverview` read model and the query that reads
it, and `OrdersModule` registers the declaration the slice exposes.

The EF-backed `OrderSummary` projection keeps its entity and `DbContext` mapping in
`Platform/`, because EF migrations are generated from the context and a migration
already exists; the projection declaration and its queries move into the slice.

### Boundaries

Every slice keeps the whole-order (or whole-payment) boundary it has today:
`DcbQuery.For(Tags.Order, orderId)`.

The boundary is the conflict check, not merely a read filter. Narrowing the type
axis per slice — `.WithType<OrderConfirmed>()` and friends — lets slices whose
types do not overlap commit concurrently, which is the point of DCB, but it also
means any event type that could invalidate the decision must be listed or the
decision silently loses updates. That is a per-slice argument, and making it
implicitly while moving files is how a refactor becomes an incident. Each slice
names its boundary on one line, so narrowing later is a local edit with a local
justification.

### Verification

The K6 load tests pin the GraphQL schema: `mutation CreateOrder($input: CreateOrderInput!)`,
`query GetRecentOrders($limit: Int!)` and the rest. The schema must come out
byte-identical — same operation names, same input type names, same result shapes.
Method names, type names and `[GraphQLDescription]` text carry over verbatim.

Checks, in order: `dotnet build`; an inventory of `[Query]`/`[Mutation]` method
names and input/result type names diffed against the pre-refactor files;
`dotnet test`; and, when Docker is available, booting the AppHost and diffing the
served SDL against a capture taken before the change.

## Non-goals

- No change to `src/Alberto.Dcb*`. If the refactor seems to need one, that is a
  finding to report, not a change to make.
- No narrowing of DCB boundaries (see above).
- No new GraphQL operations, no renamed fields, no schema drift.
- No change to the event schema, tags or stored data. The refactor is invisible to
  an existing database.
