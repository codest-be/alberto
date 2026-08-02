<img src="https://raw.githubusercontent.com/codest-be/alberto/main/icon.png" alt="" width="96" align="right">

# Alberto

[![CI](https://github.com/codest-be/alberto/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/codest-be/alberto/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Alberto?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Alberto)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Licence](https://img.shields.io/badge/licence-MIT-blue)](https://github.com/codest-be/alberto/blob/main/LICENSE)

> **Early release — under active testing.** Alberto is in its first public `0.x` versions. It
> builds clean, the suite is green, and it has not yet been run in anger by anyone but its author.
> Treat it as something to evaluate and experiment with, not to put in front of production traffic
> yet. The API will make breaking changes before 1.0. See
> [Project status](#project-status) for what that means in practice.

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

**[Start here → docs/getting-started.md](https://github.com/codest-be/alberto/blob/main/docs/getting-started.md)** — a runnable 60-line sample, no
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
  See [docs/projections.md](https://github.com/codest-be/alberto/blob/main/docs/projections.md#rebuilding-a-projection).
- **Multi-tenancy that reaches the SQL.** Tenant isolation is enforced in the queries and the
  leases, not by a filter you might forget. See [docs/multi-tenancy.md](https://github.com/codest-be/alberto/blob/main/docs/multi-tenancy.md).
- **An outbox.** A processor turns committed events into outbox rows on the same pipeline as your
  projections, and a relay claims them with `FOR UPDATE SKIP LOCKED`. Messages are derived from
  events that are already durable, so at-least-once delivery needs no distributed transaction.
  See [docs/reactors-and-outbox.md](https://github.com/codest-be/alberto/blob/main/docs/reactors-and-outbox.md).
- **An operator CLI.** Inspect checkpoints, events, projections, dead letters and tenant leases;
  rewind a processor; retry or dismiss dead letters; run a rebuild. Mutating commands confirm before
  they act and most of them take `--dry-run`; every command that reports takes `--json`, so the tool
  you use interactively is the one your runbooks call. See [docs/operations.md](https://github.com/codest-be/alberto/blob/main/docs/operations.md).
- **OpenTelemetry throughout** — traces across the append→consume seam and metrics for lag,
  conflicts, retries and dead letters.

## Install

Packages are on [nuget.org](https://www.nuget.org/packages/Alberto). Take the core plus one backend:

```bash
dotnet add package Alberto
```

```bash
dotnet add package Alberto.Postgres
```

| Package | What it gives you |
|---|---|
| `Alberto` | Event store abstractions, control loop, middleware, projections, tenancy |
| `Alberto.Commands` | The `AlbertoStore` command pipeline (`Handle → Load → Decide → Commit`) |
| `Alberto.InMemory` | In-memory backend, checkpoint, dead-letter and state stores — dev and tests |
| `Alberto.Postgres` | PostgreSQL backend, migrations, leases |
| `Alberto.EntityFramework` | EF Core–backed projections |
| `Alberto.Messaging` | Transactional outbox abstractions |
| `Alberto.Messaging.Postgres` | PostgreSQL outbox store |
| `Alberto.Telemetry` | OpenTelemetry tracing and metrics |
| `Alberto.Testing` | In-memory test helpers (`InMemoryAlbertoModule`, assertion extensions) |
| `Alberto.Testing.Xunit` | xUnit v3 test fixtures and collection definitions |

All libraries target **net10.0**. The operator CLI (`alberto`) is not a NuGet tool package; run
it from the repo with `dotnet run --project tools/Alberto.Cli`.

## Sixty seconds

```csharp
services.AddAlberto("tickets", builder => builder
    .WithInMemory()                                       // or .WithPostgres(...)
    .WithEventsFrom(Assembly.GetExecutingAssembly())       // discovers [EventType] events
    .AddProjection(OccupancyProjection.Declaration, _ => _ => occupancy)
    .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(50) }));
```

Nothing is registered until the host starts — declaration, configuration overlay, validation, and
service registration happen in three distinct phases. See
[docs/configuration.md](https://github.com/codest-be/alberto/blob/main/docs/configuration.md) for the full picture. All knobs are also
overridable from `Alberto:Modules:{moduleKey}:{Section}:{Property}` in `appsettings.json`.

The full, runnable version of that program is
[docs/getting-started.md](https://github.com/codest-be/alberto/blob/main/docs/getting-started.md) — it needs no Docker and no connection string.

## Documentation

| | |
|---|---|
| [Getting started](https://github.com/codest-be/alberto/blob/main/docs/getting-started.md) | A complete runnable sample, built up piece by piece |
| [Concepts](https://github.com/codest-be/alberto/blob/main/docs/concepts.md) | Events, tags, queries, boundaries, positions, checkpoints |
| [Event schema versioning](https://github.com/codest-be/alberto/blob/main/docs/events.md) | Permanent slugs, the `_version` tag, upcasters and their limits |
| [Projections](https://github.com/codest-be/alberto/blob/main/docs/projections.md) | Declaring them, storing them, rebuilding them live |
| [Reactors and the outbox](https://github.com/codest-be/alberto/blob/main/docs/reactors-and-outbox.md) | Side effects and publishing to the outside world |
| [Multi-tenancy](https://github.com/codest-be/alberto/blob/main/docs/multi-tenancy.md) | Tenant isolation, leases, and what it costs |
| [Operations](https://github.com/codest-be/alberto/blob/main/docs/operations.md) | The `alberto` CLI, dead letters, error policy, telemetry |
| [Backup and recovery](https://github.com/codest-be/alberto/blob/main/docs/backup-and-recovery.md) | What is truth, what is derived, and what a restore invalidates |
| [Configuration reference](https://github.com/codest-be/alberto/blob/main/docs/configuration.md) | Three-phase pipeline, all options, validation codes, custom backends |
| [Async processing architecture](https://github.com/codest-be/alberto/blob/main/docs/architecture/async-processing.md) | How the control loop actually works |
| [Tenant sharding](https://github.com/codest-be/alberto/blob/main/docs/architecture/tenant-sharding.md) | Spreading a module's tenants over several databases |
| [Migrating to 1.0](https://github.com/codest-be/alberto/blob/main/docs/migrating-to-1.0.md) | Every breaking change on the road to 1.0, most recent first |
| [Releasing](https://github.com/codest-be/alberto/blob/main/docs/releasing.md) | Versioning policy, milestones, release and backport process |

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

Alberto is **pre-1.0 and under active testing**. `0.1.0` is the first version published to
nuget.org.

**What that means, concretely:**

- **Expect breaking changes.** The public API is not frozen until 1.0. Every break is recorded in
  [CHANGELOG.md](https://github.com/codest-be/alberto/blob/main/CHANGELOG.md), with the road to 1.0
  collected in [docs/migrating-to-1.0.md](https://github.com/codest-be/alberto/blob/main/docs/migrating-to-1.0.md) —
  but there will be breaks, and some will be in the core append and
  projection APIs. Pin an exact version and read the release notes before you move.
- **It is well tested, not yet well proven.** The suite covers the libraries with unit tests plus
  Testcontainers-backed PostgreSQL integration tests, and it is green. That is evidence the code
  does what its author intended — it is not the same thing as having survived other people's
  production workloads, which it has not yet done.
- **Please try it and report what breaks.** Evaluation, prototypes and side projects are exactly
  the workloads this release is asking for. Bug reports and API feedback now are worth far more
  than after 1.0 freezes the surface.

The multi-database tenant sharding feature is marked **experimental** (`[Experimental("ALB9001")]`
on all public sharding types), which is a step beyond the general pre-1.0 caveat: it ships and its
tests pass, but the API may change more sharply than the rest of the library.

The admin surface is deliberately **not published**. `Alberto.Admin` and `Alberto.Admin.Postgres`
build and are tested, but they stay off nuget.org until the GraphQL API, MCP server and console
that consume them ship — releasing the abstraction at 1.0 would freeze it under semver before its
consumers exist.

Outbox claims are time-bounded and token-fenced: a relay crash leaves a recoverable `processing`
row, and a stale relay cannot overwrite a newer claim. Delivery remains at-least-once; see
[docs/reactors-and-outbox.md](https://github.com/codest-be/alberto/blob/main/docs/reactors-and-outbox.md#claim-leases-and-relay-crashes).

## Contributing

Bug reports and API feedback are the most useful thing you can send right now. Issues go on the
[issue tracker](https://github.com/codest-be/alberto/issues); before opening a pull request, read
[CONTRIBUTING.md](https://github.com/codest-be/alberto/blob/main/CONTRIBUTING.md) — it covers the
build, the public-API tracking files a change has to update, the code style, and the event
deserialization rule. Participation is governed by the
[Code of Conduct](https://github.com/codest-be/alberto/blob/main/CODE_OF_CONDUCT.md).

Security vulnerabilities do **not** go on the issue tracker. Use GitHub's private vulnerability
reporting, as described in
[SECURITY.md](https://github.com/codest-be/alberto/blob/main/SECURITY.md).

## Licence

[MIT](https://github.com/codest-be/alberto/blob/main/LICENSE).
