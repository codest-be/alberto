# Projections

A projection turns the log into something cheap to query. It is a pure function from
`(state, event) → state`, run by the control loop, with a checkpoint of its own so it can be
paused, rewound or rebuilt without touching anything else.

Alberto has two families:

| | Declared projections | EF projections |
|---|---|---|
| State is | Any type, stored as JSON | An EF entity, in your own table |
| Store | `PostgresStateStore<T>` / `InMemoryStateStore<T>` | `EfStateStore<TEntity, TDbContext>` |
| Register with | `AddProjection` | `AddEfProjection` |
| Query it with | The store's `LoadManyAsync`, by document id | LINQ over your `DbSet` |
| Good for | Documents, counters, summaries you look up by key | Anything you want to filter, join or sort in SQL |

Both are driven by the same declaration API and both support live rebuilds.

## Declaring one

```csharp
public static class OrdersOverviewProjection
{
    public static readonly ProjectionDeclaration<OrdersOverview> Declaration =
        DeclareProjection.For<OrdersOverview>(nameof(OrdersOverviewProjection))
            .Collection("orders_overview")
            .InitialState(() => new OrdersOverview())
            .On<OrderPlaced>(
                id:    _ => "overview",
                apply: (state, e, ctx) => state with
                {
                    TotalOrders = state.TotalOrders + 1,
                    TotalValue  = state.TotalValue + e.Amount,
                })
            .On<OrderCancelled>(
                id:    _ => "overview",
                apply: (state, _, _) => state with { TotalOrders = state.TotalOrders - 1 })
            .Build();
}
```

- **`For<TState>(processorId)`** — the processor id is the projection's identity: its checkpoint
  key, what `alberto status` lists, what you pass to `alberto ops rebuild start`. Use
  `nameof(TheProjectionClass)` so it survives renames visibly rather than silently.
- **`Collection(name)`** — optional; defaults to the processor id. It names the logical document
  set inside `alberto_projection_states`.
- **`InitialState(factory)`** — optional; defaults to `new TState()`. `TState` must have a
  parameterless constructor.
- **`On<TEvent>(id, apply)`** — `id` selects the document this event updates, and returning `null`
  skips the event entirely. `apply` gets the current state, the parsed event, and a
  `ProjectionContext` carrying `EventId`, `Position`, `Timestamp`, `TenantId` and `Metadata`.

`apply` returns a `ProjectionResult<TState>`:

```csharp
state with { Total = state.Total + 1 }      // implicit → Set
ProjectionResults.Delete<OrdersOverview>()  // remove the document
ProjectionResults.Unchanged<OrdersOverview>()  // no write at all
```

`Unchanged` is worth reaching for: it skips the write, not just the value change.

### Keep `apply` pure

It must be a function of its arguments alone. No clock, no `Guid.NewGuid()`, no HTTP calls, no
reads of other documents. A rebuild replays the entire log through this function and has to arrive
at the same answer; anything ambient makes the rebuilt copy differ from the live one. Side effects
belong in a [reactor](reactors-and-outbox.md).

## Storing it

`AddProjection` pairs the declaration with a factory for where state lands:

```csharp
.AddProjection(OrdersOverviewProjection.Declaration, ctx =>
{
    var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
    return () => new PostgresStateStore<OrdersOverview>(
        dataSource,
        projectionType: nameof(OrdersOverviewProjection),
        schema: "orders",
        rebuildVersion: ctx.RebuildVersion);
})
```

The factory returns a *factory* because a store is created per tenant and per rebuild version, not
once. `ctx` is a `ProjectionStoreContext` with two members:

- `ctx.Services` — the provider.
- `ctx.RebuildVersion` — a `Func<int>`. **Pass it through; never call it and cache the result.**
  Promotion changes the answer underneath a store that is already running.

In tests and samples, swap the store and change nothing else:

```csharp
var overview = new InMemoryStateStore<OrdersOverview>();
.AddProjection(OrdersOverviewProjection.Declaration, _ => () => overview)
```

### Reading it back

`IStateStore<TState>` is deliberately small — two methods, both keyed by document id:

```csharp
Task<Dictionary<string, TState>> LoadManyAsync(IEnumerable<string> documentIds,
                                               CancellationToken ct = default);
Task ApplyChangesAsync(IReadOnlyDictionary<string, TState> upserts,
                       IReadOnlyCollection<string> deletes,
                       CancellationToken ct = default);
```

Each adapter owns the transaction it applies a change set under; the interface does not hand one
across, because a projection processor commits its own batch and there is nothing outside it to
enlist in.

That is the whole read surface: **fetch documents whose ids you already know.** There is no list,
no filter and no sort. Wanting any of those is the signal to use an EF projection instead of
forcing it through a document store — with EF you get LINQ over a `DbSet` and real columns to
index, which is the trade the two options exist to let you make.

**Building a store outside the module builder** — a query handler, a GraphQL resolver — is where a
subtle bug lives. A store constructed with the default rebuild version pins itself to version 1 and
keeps serving the *pre-rebuild* copy forever after a promotion. Use `ProjectionVersions.LiveVersion`:

```csharp
new PostgresStateStore<OrdersOverview>(
    dataSource,
    projectionType: nameof(OrdersOverviewProjection),
    schema: "orders",
    rebuildVersion: ProjectionVersions.LiveVersion(sp, ModuleKey, nameof(OrdersOverviewProjection)),
    tenantId: tenantId);
```

It resolves to version 1 forever in a module with no rebuild pipeline, so it is safe to use
unconditionally. Note the named arguments — `PostgresStateStore`'s parameters are all optional
strings and a positional mistake binds silently.

## EF projections

When the read model wants to be a real table you can filter, join and page over:

```csharp
public sealed class OrderSummaryEntity : IProjectionEntity
{
    public string DocumentId { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public long LastProcessedPosition { get; set; }
    public uint Version { get; set; }             // EF concurrency token
    public int RebuildVersion { get; set; }

    public string CustomerName { get; set; } = "";
    public decimal Total { get; set; }
}
```

Configure it in `OnModelCreating` — **this is not optional**:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ProjectionEntity<OrderSummaryEntity>(entity =>
    {
        entity.ToTable("order_summaries");
        entity.Property(e => e.CustomerName).HasMaxLength(200);
    });
}
```

`ProjectionEntity<T>()` makes the key `(DocumentId, RebuildVersion)`, defaults the version column
to 1 so existing rows keep working, and adds a `(RebuildVersion, UpdatedAt)` index. Without it, a
shadow rebuild's rows collide with the live rows on insert and the rebuild overwrites the very
projection it was shadowing. Adding it to an existing model is a schema change — generate a
migration.

Then wire the module and the projection:

```csharp
.WithEntityFramework<OrdersDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders")))
.AddEfProjection<OrderSummaryEntity, OrdersDbContext>(OrderSummaryEfProjection.Declaration)
```

EF projections batch: a whole control-loop batch accumulates in the change tracker and flushes with
one `SaveChanges`.

### Inline vs async

```csharp
.AddEfProjection<OrderSummaryEntity, OrdersDbContext>(decl, ProjectionMode.Inline)
```

| | `Async` (default) | `Inline` |
|---|---|---|
| Runs | On the control loop, own checkpoint | Inside `AppendAsync`, before it returns |
| Consistency | Eventual (≈ polling interval) | Read-your-writes |
| A failure | Dead-letters; the mutation still succeeded | Throws out of `AppendAsync` |
| Latency cost | None on the write path | Its write latency, added to every mutation |
| Rebuildable | Yes | **No** — inline projections register no state clearer |

One thing "inline" does *not* mean: atomic. Inline projections run **after** the append transaction
has committed, on their own connection. A projection that throws therefore fails the caller's
`AppendAsync` even though the events are already durable — the caller sees an exception, the log
sees a successful append. Design for that: an inline projection should be simple enough that it
only fails when the database is down.

Use `Inline` only for narrow per-user projections the caller will immediately re-query. Everything
else should be `Async`; "the UI flickers" is better solved by returning the decision's own result
than by putting a projection on the write path.

An async projection can still be read straight after a write without sleeping for it:

```csharp
var projections = sp.GetRequiredKeyedService<ProjectionCatchUp>("orders");
await projections.WaitForProjectionAsync("order-summary");
```

It reads the store's head once and returns as soon as that processor's checkpoint has passed it,
throwing `TimeoutException` rather than serving a stale read. It watches a checkpoint the local
control loop advances, so it reports progress made *in this process* — on a replica that does not
run the processor it waits out its timeout however far along the projection actually is.

## Rebuilding a projection

Change how a projection interprets history and its stored state is now wrong. A rebuild replays the
whole log into a **second copy** while the live copy keeps serving reads, then swaps them in one
transaction. Readers move from a complete old projection to a complete new one; there is no window
where it is empty or half-built.

```
              live loop ──────────────────────────────▶  version 1  ◀── readers
                                                              │
  start ──▶  shadow loop (own checkpoint, from position 0) ─▶ version 2
                                                              │
  promote ──▶ version 2 becomes active, version 1 deleted ────┘  (one transaction)
```

### Enabling it

```csharp
.WithControlLoop(loop => loop.WithRebuilds())
```

Plus the two requirements you have already met if you followed the sections above:

1. Document stores resolve their version through `ctx.RebuildVersion`.
2. EF entities are configured with `ProjectionEntity<T>()`.

A projection meeting neither will have its **live state overwritten by the replay** instead of
shadowed. There is no way for the coordinator to detect this for you.

`WithRebuilds()` registers the machinery but starts nothing. `WithRebuilds(autoPromote: false)`
parks a finished rebuild at `ready` until an operator promotes it.

### Running one

```bash
alberto ops rebuild start OrdersOverviewProjection
alberto ops rebuild status
alberto ops rebuild promote OrdersOverviewProjection   # only needed with autoPromote: false
alberto ops rebuild abort OrdersOverviewProjection
```

**The replay runs in your application, not in the CLI.** The CLI only moves the state machine. A
module without `WithRebuilds()` will leave a started rebuild sitting at `rebuilding` forever.

The coordinator holds no state of its own — everything derives from
`alberto_projection_rebuild_meta` — so a rebuild started from one process is picked up by another,
and a coordinator that crashes mid-rebuild resumes on restart.

### Limits

- One rebuild per processor at a time; two operators racing cannot both win.
- Version numbers only ever go up, including across aborts, so `alberto ops rebuild status` will
  show gaps after a few abandoned attempts. That is deliberate: an abort cannot stop the shadow
  loop in the application process synchronously, so handing its number to the next rebuild would
  let its last few writes seed the replay.
- Run more than one replica of the module and you need leases enabled
  (`.WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true } })`), or two replicas
  replay into the same version.
- A reader that resolves the active version and *then* queries can find nothing, if the promotion
  lands between the two steps: promotion deletes the superseded rows in the same transaction that
  flips the version, so the number the reader is holding stops existing. The window is a single
  query and only opens at the moment of a promotion, but it is real. Retry a read that comes back
  empty when the document should exist.
- A rebuild reprocesses every event. **Reactors are not rebuilt** — replaying side effects is not
  something the coordinator can make safe.
- Inline projections cannot be rebuilt this way.

The mechanics are in
[architecture/async-processing.md](architecture/async-processing.md#projection-rebuilds).

## Choosing a document id

`id: e => …` is the sharpest design decision in a projection, because it decides the write
contention and the read shape at once:

| `id` returns | You get | Watch out for |
|---|---|---|
| A constant (`"overview"`) | One global counter document | Every event writes the same row |
| An entity id | One document per entity | Usually right |
| A composite (`$"{tenant}:{month}"`) | One per bucket | Ids are strings; keep them stable forever |
| `null` | The event is skipped | The intended way to filter |

Ids are opaque strings. Anything you can compute from the event alone is fair game — but changing
the scheme later means a rebuild, because the old documents are keyed the old way.
