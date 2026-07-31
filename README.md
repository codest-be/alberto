# Alberto

An event store for .NET where the consistency boundary is a **query**, not an aggregate.

```csharp
// "Nobody else may have touched seat A12 of this show while I was deciding."
var boundary = DcbQuery.ByAllTags(
    new EventTag("show", showId.ToString()),
    new EventTag("seat", "A12"));

await store.Handle(new ReserveSeat(showId, "A12", customerId))
    .Load(boundary, new SeatState(), Seat.Apply)
    .Decide((cmd, state) => state.IsTaken
        ? Problem.Create("seat.taken", $"Seat {cmd.Seat} is already reserved.")
        : Decision.Succeed(new SeatReserved(cmd.ShowId, cmd.Seat, cmd.CustomerId)))
    .Commit(ct);
```

That block loads exactly the events the decision depends on, folds them into state, decides, and
appends — refusing the append if anything matching that same query landed in between. No aggregate
root, no stream to pick in advance.

**[Start here → docs/getting-started.md](docs/getting-started.md)** — a runnable 60-line sample, no
database required.

---

## Why DCB

Classical event sourcing makes you choose a stream per event *before* you know what your decisions
will need. That choice is hard to undo, because the stream is simultaneously your storage layout,
your consistency unit, and your replay unit. Two rules that need to see the same event from
different angles force you into either duplicated events, saga choreography, or one big
coarse-grained aggregate that serialises unrelated work.

Dynamic Consistency Boundaries (DCB) split those jobs apart. Events are written once to a single
ordered log and tagged with every concept they concern:

```csharp
[EventType("seat-reserved")]
public sealed record SeatReserved(
    [property: Tag("show")]     Guid ShowId,
    [property: Tag("seat")]     string Seat,
    [property: Tag("customer")] Guid CustomerId) : IEvent;
```

Each decision then declares its own boundary as a query over those tags. "This seat at this show"
and "everything this customer has ever booked" are both first-class boundaries over the same
event — no duplication, no coordination between them. Two people reserving different seats never
contend; two people reserving the same seat always do.

## Why Alberto specifically

- **Postgres, and nothing else.** The whole store is a handful of tables and functions in the
  database your application already has — no broker, no separate event-store server. The boundary
  check and the append run in one transaction under a transaction-scoped advisory lock, so a
  conflicting concurrent write is rejected rather than interleaved.
- **A real async pipeline, not a `foreach`.** One control loop per module reads batches, dispatches
  through a middleware chain, retries with exponential backoff, dead-letters poison events, and
  splits a failing batch to isolate the one event that broke.
- **Zero-downtime projection rebuilds.** Change how a projection reads history, then replay the
  whole log into a shadow copy while the live one keeps serving reads, and swap them in one
  transaction. Driven by the CLI, executed by your running application.
  See [docs/projections.md](docs/projections.md#rebuilding-a-projection).
- **Multi-tenancy that reaches the SQL.** Tenant isolation is enforced in the queries and the
  leases, not by a filter you might forget. See [docs/multi-tenancy.md](docs/multi-tenancy.md).
- **An outbox.** A processor turns committed events into outbox rows on the same pipeline as your
  projections, and a relay claims them with `FOR UPDATE SKIP LOCKED`. Messages are derived from
  events that are already durable, so at-least-once delivery needs no distributed transaction.
  See [docs/reactors-and-outbox.md](docs/reactors-and-outbox.md).
- **An operator CLI.** Inspect checkpoints, events, projections, dead letters and tenant leases;
  rewind a processor; retry or dismiss dead letters; run a rebuild. Mutating commands confirm before
  they act and most of them take `--dry-run`; every command takes `--json`, so the tool you use
  interactively is the one your runbooks call. See [docs/operations.md](docs/operations.md).
- **OpenTelemetry throughout** — traces across the append→consume seam and metrics for lag,
  conflicts, retries and dead letters.

## Install

Packages are published to **GitHub Packages** for the `codest-be` organisation. Add the feed:

```bash
dotnet nuget add source "https://nuget.pkg.github.com/codest-be/index.json" --name alberto --username <your-github-username> --password <a-github-PAT-with-read:packages>
```

Then take the core plus one backend:

```bash
dotnet add package Alberto.Dcb --prerelease
```

| Package | What it gives you |
|---|---|
| `Alberto.Dcb` | Event store abstractions, control loop, middleware, projections, tenancy |
| `Alberto.Dcb.Commands` | The `AlbertoStore` command pipeline (`Handle → Load → Decide → Commit`) |
| `Alberto.Dcb.InMemory` | In-memory backend, checkpoint, dead-letter and state stores — dev and tests |
| `Alberto.Dcb.Postgres` | PostgreSQL backend, migrations, leases |
| `Alberto.Dcb.EntityFramework` | EF Core–backed projections |
| `Alberto.Dcb.Messaging` | Transactional outbox abstractions |
| `Alberto.Dcb.Postgres.Messaging` | PostgreSQL outbox store |
| `Alberto.Dcb.Telemetry` | OpenTelemetry tracing and metrics |
| `Alberto.Cli` | The `alberto` operator CLI (`dotnet tool install`) |

All libraries target **net10.0**. The CLI is a net10.0 tool.

## Sixty seconds

```csharp
services.AddAlberto("tickets", builder => builder
    .WithInMemory()                                       // or .WithPostgres(...)
    .WithEventsFrom(Assembly.GetExecutingAssembly())       // discovers [EventType] events
    .AddProjection(OccupancyProjection.Declaration, _ => () => occupancy)
    .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(50) }));
```

Nothing is registered until the host starts — declaration, configuration overlay, validation, and
service registration happen in three distinct phases. See
[docs/configuration.md](docs/configuration.md) for the full picture. All knobs are also
overridable from `Alberto:Modules:{moduleKey}:{Section}:{Property}` in `appsettings.json`.

The full, runnable version of that program is
[docs/getting-started.md](docs/getting-started.md) — it needs no Docker and no connection string.

## Documentation

| | |
|---|---|
| [Getting started](docs/getting-started.md) | A complete runnable sample, built up piece by piece |
| [Concepts](docs/concepts.md) | Events, tags, queries, boundaries, positions, checkpoints |
| [Event schema versioning](docs/events.md) | Permanent slugs, the `_version` tag, upcasters and their limits |
| [Projections](docs/projections.md) | Declaring them, storing them, rebuilding them live |
| [Reactors and the outbox](docs/reactors-and-outbox.md) | Side effects and publishing to the outside world |
| [Multi-tenancy](docs/multi-tenancy.md) | Tenant isolation, leases, and what it costs |
| [Operations](docs/operations.md) | The `alberto` CLI, dead letters, error policy, telemetry |
| [Configuration reference](docs/configuration.md) | Three-phase pipeline, all options, validation codes, custom backends |
| [Async processing architecture](docs/architecture/async-processing.md) | How the control loop actually works |
| [Tenant sharding](docs/architecture/tenant-sharding.md) | Spreading a module's tenants over several databases |
| [Upgrade notes](UPGRADING.md) | Every breaking change, most recent first |

## Repository layout

```
/src        Packable core libraries
/apps       Example applications — Orders (run by .NET Aspire) and Payments (a library the Orders API reads from)
/tools      The alberto operator CLI
/tests      xUnit v3 unit + Testcontainers integration tests, and K6 load tests
```

Run the whole example stack — Postgres, migrations, and the Orders GraphQL API — with:

```bash
dotnet run --project apps/Alberto.AppHost
```

## Project status

Alberto is **pre-1.0** and published as `0.1.0-beta.*`. The whole solution builds clean and the
suite is green; the API still moves between betas, and every break is recorded in
[UPGRADING.md](UPGRADING.md).

The multi-database tenant sharding feature is marked **experimental** (`[Experimental("ALB9001")]`
on all public sharding types). It ships and the tests pass, but the API may change more sharply
than the rest of the library between betas.

Outbox claims are time-bounded and token-fenced: a relay crash leaves a recoverable `processing`
row, and a stale relay cannot overwrite a newer claim. Delivery remains at-least-once; see
[docs/reactors-and-outbox.md](docs/reactors-and-outbox.md#claim-leases-and-relay-crashes).

## Licence

MIT.
