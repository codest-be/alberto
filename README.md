<img src="https://raw.githubusercontent.com/codest-be/alberto/main/icon.png" alt="" width="96" align="right">

# Alberto

[![CI](https://github.com/codest-be/alberto/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/codest-be/alberto/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Alberto?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Alberto)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Licence](https://img.shields.io/badge/licence-MIT-blue)](https://github.com/codest-be/alberto/blob/main/LICENSE)

> **Early release, under active testing.** Alberto is in its first public `0.x` versions. The
> suite is green, but nobody except its author has run it in anger. Evaluate it and experiment
> with it; do not put it in front of production traffic yet. The API will break before 1.0. See
> [Project status](#project-status).

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
appends, refusing the append if anything matching that same query landed in between. No aggregate
root, no stream to pick in advance.

**[Start here → docs/getting-started.md](https://github.com/codest-be/alberto/blob/main/docs/getting-started.md)**.
A runnable 60-line sample, no database required.

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
event, with no duplication and no coordination between them. Two people reserving different seats
never contend; two people reserving the same seat always do.

## Why Alberto specifically

- **Postgres, and nothing else.** The whole store is a handful of tables and functions in the
  database your application already has. No broker, no separate event-store server. The boundary
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
- **OpenTelemetry throughout.** Traces across the append→consume seam, and metrics for lag,
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
| `Alberto.InMemory` | In-memory backend, checkpoint, dead-letter and state stores, for dev and tests |
| `Alberto.Postgres` | PostgreSQL backend, migrations, leases |
| `Alberto.EntityFramework` | EF Core-backed projections |
| `Alberto.Messaging` | Transactional outbox abstractions |
| `Alberto.Messaging.Postgres` | PostgreSQL outbox store |
| `Alberto.Telemetry` | OpenTelemetry tracing and metrics |
| `Alberto.Testing` | Test helpers: an in-memory module harness, quiescence polling, an in-memory outbox |
| `Alberto.Testing.Xunit` | The conformance suite Alberto runs against its own backends, for you to run against yours |

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

Nothing is registered until the host starts. Declaration, configuration overlay, validation, and
service registration happen in three distinct phases; see
[docs/configuration.md](https://github.com/codest-be/alberto/blob/main/docs/configuration.md).
All knobs are also overridable from `Alberto:Modules:{moduleKey}:{Section}:{Property}` in
`appsettings.json`.

The full, runnable version of that program is
[docs/getting-started.md](https://github.com/codest-be/alberto/blob/main/docs/getting-started.md).
It needs no Docker and no connection string.

## Testing your application

```bash
dotnet add package Alberto.Testing
```

### Deciders and evolvers, with no infrastructure at all

A decision function is pure: past events in, events or problems out. `Spec` tests it as such —
no host, no event store, no `await`. Past events go in `Given`, the decider goes in `When`, and
the `Then` verbs assert on what came back:

```csharp
Spec.For(new ConfirmOrderEvolver())
    .Given(new OrderCreated(orderId, customerId, [widget], null))
    .When(state => ConfirmOrderDecider.Decide(state, now))
    .ThenEmitsOnly<OrderConfirmed>(e => e.OrderId == orderId)
    .ThenState(s => s.Status.Should().Be(OrderStatus.Confirmed));
```

`Given` folds those events through the evolver the same way the command pipeline does, so the
state under test is the state production would have built. `ThenState` folds the *emitted* events
in on top, which is how you assert that a decision leaves the aggregate somewhere sensible.

The failure verbs mirror it. Pass the same problem factory production calls, and the code stays a
single source of truth:

```csharp
Spec.For(new ConfirmOrderEvolver())
    .Given(new OrderCreated(orderId, customerId, [], null))
    .When(state => ConfirmOrderDecider.Decide(state, now))
    .ThenFails(OrderProblems.Empty());
```

| | |
|---|---|
| `Given(...)` / `Given(state, ...)` / `GivenNoEvents()` | The history. Repeated calls accumulate; the state overload starts from one you built by hand |
| `When(state => ...)` | The decider. An overload takes `Func<TState, Decision<TResult>>` |
| `ThenSucceeds()` / `ThenFails()` / `ThenFails(code)` / `ThenFails(problem)` | The outcome. `ThenFails(problem)` compares `Code` only |
| `ThenEmits<T>(match?)` / `ThenEmitsOnly<T>(match?)` / `ThenEmitsNothing()` | What was recorded. `ThenEmits` says nothing about the other events; `ThenEmitsOnly` requires this to be the only one |
| `ThenState(...)` / `ThenResult<T>(...)` | The state after the emitted events fold in, and the value a `Decision<T>` carried |
| `ThenEvents(...)` / `ThenProblems(...)` | The escape hatches, handing you the lists in order |

`Spec.Stateless()` drops `Given` and `ThenState` for decisions that fold no history. Every verb
returns the concrete specification rather than a base type, so a chain never narrows — you can
still reach `ThenState` after `ThenSucceeds`.

Failures throw `SpecificationException` with the decision in the message: expecting
`order.not-found` from a decider that returned `order.cancellation-reason-required` names both.
Like the rest of the package it calls no test framework's `Assert`, so it reads the same from
xUnit, NUnit, TUnit or MSTest.

### Whole modules, over the in-memory backend

The control loop is asynchronous, so a test that appends an event and asserts on a projection in
the next line is asserting on a race. `AlbertoTestHarness` exists to make the correct sequence —
append, wait, assert — shorter than the incorrect one. It runs a real module over the in-memory
backend, so your production `AddAlberto` configuration is what the test exercises:

```csharp
await using var harness = await AlbertoTestHarness.StartAsync("tickets", builder => builder
    .WithInMemory()
    .WithEventsFrom(typeof(SeatReserved).Assembly)
    .AddProjection(OccupancyProjection.Declaration, _ => _ => occupancy));

await harness.AppendAsync(new SeatReserved(showId, seat), [new EventTag("show", showId)]);
await harness.WaitForQuiescenceAsync();   // every processor's checkpoint has reached the head

var states = await occupancy.LoadManyAsync([showId]);
```

`WaitForQuiescenceAsync` throws `TimeoutException` rather than returning quietly, so a projection
that never catches up fails where it broke instead of in an unrelated assertion later.
`Poll.UntilAsync` covers conditions the harness cannot see, `EventCollector` captures what was
projected, and `InMemoryOutboxStore` stands in for a relay in reactor tests.

Two rules tend to bite on the first test:

- **Module keys are Alberto identifiers**, not free text: lowercase letters, digits and
  underscores, starting with a letter, 63 characters at most. `"ticket_shop"` is a module key;
  `"ticket-shop"` throws `ArgumentException`. The key is composed into DI service keys
  (`{moduleKey}:{processorId}`, `{moduleKey}#{shardId}`) and used as a PostgreSQL schema name, so
  the rule is not stylistic. Tenant ids and shard ids are validated by the same rule; processor
  ids are not, and may contain dots and hyphens.
- **The `state` handed to a projection's `apply` is never null.** For a document that does not
  exist yet it is the initial state — `new TState()` unless you supplied `InitialState(...)` — so
  write `state.Reserved + 1`, not `state?.Reserved ?? 0`.

### Your own backend, against Alberto's own suite

If you write one — an event store, state store, checkpoint store, dead-letter store or outbox —
`Alberto.Testing.Xunit` is the suite Alberto runs against its own. Derive from the specification
and implement one factory method; xUnit discovers the facts from the base class:

```csharp
public sealed class MyStoreTests : EventStoreBackendSpecification
{
    protected override async Task<IEventStoreBackend> CreateBackend() => new MyBackend(...);
}
```

That inherits 38 facts for an event store backend, 26 for a state store, 15 for an outbox, 9 each
for a checkpoint or dead-letter store, and 8 more for a claimable dead-letter store. Its xUnit and
FluentAssertions references are `PrivateAssets="all"`, so nothing test-only reaches your
application's dependency graph.

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
| [Message transports](https://github.com/codest-be/alberto/blob/main/docs/architecture/message-transports.md) | Why no broker binding ships, and how to write the adapter |
| [Migrating to 1.0](https://github.com/codest-be/alberto/blob/main/docs/migrating-to-1.0.md) | Every breaking change on the road to 1.0, most recent first |
| [Releasing](https://github.com/codest-be/alberto/blob/main/docs/releasing.md) | Versioning policy, milestones, release and backport process |

## Repository layout

```
/src        Packable core libraries
/apps       Examples: Orders (run by .NET Aspire) and Payments (a library the Orders API reads from)
/tools      The alberto operator CLI
/tests      xUnit v3 unit + Testcontainers integration tests, and K6 load tests
```

Run the whole example stack (Postgres, migrations, and the Orders GraphQL API) with:

```bash
dotnet run --project apps/Alberto.AppHost
```

## Project status

Alberto is **pre-1.0 and under active testing**. `0.1.0` is the first version published to
nuget.org.

- **Expect breaking changes.** The public API is not frozen until 1.0, and some breaks will land in
  the core append and projection APIs. Every one is recorded in
  [CHANGELOG.md](https://github.com/codest-be/alberto/blob/main/CHANGELOG.md), with the road to 1.0
  collected in [docs/migrating-to-1.0.md](https://github.com/codest-be/alberto/blob/main/docs/migrating-to-1.0.md).
  Pin an exact version and read the release notes before you move.
- **Well tested, not yet well proven.** Unit tests plus Testcontainers-backed PostgreSQL
  integration tests, all green. That is evidence the code does what its author intended, not that
  it has survived anyone else's production workload, which it has not.
- **Please try it and report what breaks.** Evaluation, prototypes and side projects are the
  workloads this release is asking for. Feedback now is worth far more than after 1.0 freezes the
  surface.

The multi-database tenant sharding feature is marked **experimental** (`[Experimental("ALB9001")]`
on all public sharding types), a step beyond the general pre-1.0 caveat: it ships and its tests
pass, but the API may change more sharply than the rest of the library.

The admin surface is deliberately **not published**. `Alberto.Admin` and `Alberto.Admin.Postgres`
build and are tested, but they stay off nuget.org until the GraphQL API, MCP server and console
that consume them ship. Releasing the abstraction at 1.0 would freeze it under semver before its
consumers exist.

Outbox claims are time-bounded and token-fenced: a relay crash leaves a recoverable `processing`
row, and a stale relay cannot overwrite a newer claim. Delivery remains at-least-once; see
[docs/reactors-and-outbox.md](https://github.com/codest-be/alberto/blob/main/docs/reactors-and-outbox.md#claim-leases-and-relay-crashes).

## Contributing

Issues go on the [issue tracker](https://github.com/codest-be/alberto/issues). Before opening a
pull request, read [CONTRIBUTING.md](https://github.com/codest-be/alberto/blob/main/CONTRIBUTING.md),
which covers the build, the public-API tracking files a change has to update, the code style, and
the event deserialization rule. Participation is governed by the
[Code of Conduct](https://github.com/codest-be/alberto/blob/main/CODE_OF_CONDUCT.md).

Security vulnerabilities do **not** go on the issue tracker. See
[SECURITY.md](https://github.com/codest-be/alberto/blob/main/SECURITY.md).

## Licence

[MIT](https://github.com/codest-be/alberto/blob/main/LICENSE).
