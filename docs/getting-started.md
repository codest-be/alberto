# Getting started

We are going to build seat reservations for a theatre. It is the smallest problem that actually
needs a consistency boundary: two people must never end up with seat A12, and two people booking
*different* seats must never wait on each other.

The whole thing runs in a console app with no database. Every snippet below is taken from a program
that was compiled and run; the finished file is at the bottom, along with the output it produces.

## 1. A project

```bash
dotnet new console -o Tickets
cd Tickets
dotnet add package Alberto --prerelease
dotnet add package Alberto.Commands --prerelease
dotnet add package Alberto.InMemory --prerelease
dotnet add package Microsoft.Extensions.Hosting
```

(If you have not added the package feed yet, see [Install](../README.md#install).)

## 2. Events

An event is a record implementing `IEvent`. Two attributes give it meaning:

- `[EventType("...")]` is its stable name on disk. Rename the C# type freely; do not rename this.
- `[Tag("concept")]` marks a property as one of the concepts the event concerns. Tags are how
  queries find events later, so tag *everything a future decision might ask about*.

```csharp
[EventType("seat-reserved")]
public sealed record SeatReserved(
    [property: Tag("show")]     Guid ShowId,
    [property: Tag("seat")]     string Seat,
    [property: Tag("customer")] Guid CustomerId) : IEvent;

[EventType("reservation-cancelled")]
public sealed record ReservationCancelled(
    [property: Tag("show")] Guid ShowId,
    [property: Tag("seat")] string Seat) : IEvent;
```

> `[Tag]` targets **properties**. On a record's primary constructor you must write
> `[property: Tag("show")]` — plain `[Tag("show")]` binds to the parameter and the tag is silently
> never written.

This one event now carries three boundaries at once: everything about a show, everything about a
seat, everything a customer did. Nothing had to be duplicated to make that true.

## 3. The boundary

A boundary is a `DcbQuery`. Ours is "every event about this one seat at this one show":

```csharp
public static DcbQuery Boundary(Guid showId, string seat) =>
    DcbQuery.ByAllTags(
        new EventTag("show", showId.ToString()),
        new EventTag("seat", seat));
```

`ByAllTags` means *intersection* — an event must carry both tags. `ByTags` would be a union, which
here would drag in every seat of the show and make unrelated reservations contend.

> **The one trap worth memorising.** Guid tag values are written with `ToString("D")` — the
> hyphenated form. Build tags with `showId.ToString()`, never `$"show:{showId:N}"`. A wrong format
> does not throw: the query simply matches nothing, your state comes back empty, and your guard
> silently never fires. This exact bug ate an hour while writing this page.

## 4. State and how to fold it

State is whatever the decision needs and nothing more. For "may I take this seat?", one bool:

```csharp
public sealed record SeatState
{
    public bool IsTaken { get; init; }
}

public static SeatState Apply(SeatState state, IEvent e) => e switch
{
    SeatReserved         => state with { IsTaken = true },
    ReservationCancelled => state with { IsTaken = false },
    _                    => state
};
```

## 5. The decision

`AlbertoStore.Handle` starts a pipeline: load, decide, commit.

```csharp
Task<Result> Reserve(string seat) =>
    store.Handle(new ReserveSeat(showId, seat, Guid.NewGuid()))
        .Load(Seat.Boundary(showId, seat), new SeatState(), Seat.Apply)
        .Decide((cmd, state) => state.IsTaken
            ? Problem.Create("seat.taken", $"Seat {cmd.Seat} is already reserved.")
            : Decision.Succeed(new SeatReserved(cmd.ShowId, cmd.Seat, cmd.CustomerId)))
        .Commit(CancellationToken.None);
```

`Load` does more than read. It folds the boundary into state *and remembers the last position it
saw*, then `Commit` appends with that query and that position as an append condition. If anything
matching the boundary was written while you were deciding, the append is rejected with a
`DcbConflictException` instead of overwriting someone's reservation. That is the whole optimistic
concurrency story — you never write a version number yourself.

Because the boundary comes from `Load`, `Commit(ct)` needs no arguments — and the compiler will not
let you call it without a `Load`. Add `.RetryOnConflict(3)` before `Commit` to re-read and re-decide
instead of surfacing the conflict, or use `.TryCommit(ct)` to get the conflict back as a
`dcb.conflict` problem rather than an exception.

Two optional stages sit in front of `Load`. `.Validate(cmd => …)` rejects the command before
anything touches the store, and `.Enrich(async (cmd, ct) => …)` replaces it with something richer —
an FX rate, a fraud score, a row from another service. Both run once, even when `RetryOnConflict`
re-runs the read-decide loop.

`Decide` returns a `Decision`: either events to append, or a `Problem`. Both convert implicitly, so
the ternary above type-checks. The result is a `Result` with `IsSuccess` and `Problems`; nothing
throws for a rejected business rule.

## 6. A read model

Decisions read a tiny boundary. Queries — "how full is this show?" — read a projection, built by a
background loop from the same log.

```csharp
public sealed record ShowOccupancy
{
    public int SeatsTaken { get; init; }
}

public static readonly ProjectionDeclaration<ShowOccupancy> Declaration =
    DeclareProjection.For<ShowOccupancy>("show-occupancy")
        .On<SeatReserved>(
            id:    e => e.ShowId.ToString(),
            apply: (state, _, _) => state with { SeatsTaken = state.SeatsTaken + 1 })
        .On<ReservationCancelled>(
            id:    e => e.ShowId.ToString(),
            apply: (state, _, _) => state with { SeatsTaken = state.SeatsTaken - 1 })
        .Build();
```

`id` picks which document the event updates — one row per show here. `apply` is a pure function
returning the new state; it can also return `ProjectionResults.Delete<ShowOccupancy>()` to remove
the document, or `ProjectionResults.Unchanged<ShowOccupancy>()` to skip the write entirely.

## 7. Wiring

```csharp
var occupancy = new InMemoryStateStore<ShowOccupancy>();
var services  = new ServiceCollection();

services.AddAlberto("tickets", builder => builder
    .WithInMemory()
    .WithEventsFrom(Assembly.GetExecutingAssembly())
    .AddProjection(OccupancyProjection.Declaration, _ => () => occupancy)
    .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(50) }));
```

- `AddAlberto(key, …)` registers one **module**. Everything inside is keyed by that string, so an
  application can host several modules with separate stores and control loops.
- `WithEventsFrom(assembly)` scans for `[EventType]` records, builds the serializer, and registers
  the `AlbertoStore` command pipeline. It comes from the separate `Alberto.Commands` package.
- `AddProjection` takes the declaration and a factory for where state goes. The factory's argument
  is a `ProjectionStoreContext` — ignored here, but it is what carries the rebuild version once you
  want live rebuilds ([projections.md](projections.md#rebuilding-a-projection)).
- `WithControlLoop` configures the background loop. Omit it entirely and you still get one on
  defaults.

`AlbertoStore` itself is registered **keyed and scoped**, under the same module key as the event
store it wraps. Resolve it with `GetRequiredKeyedService<AlbertoStore>("tickets")`, from a scope —
the request scope in a web application, or an explicit `provider.CreateScope()` in a console one.
The key is what lets one host serve several modules: each gets its own store over its own log.

Going to production is the same shape with one line swapped:

```csharp
    .WithPostgres(o => o with { ConnectionString = connectionString, Schema = "tickets" })
```

## The whole program

```csharp
using System.Reflection;
using Alberto;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ---------------------------------------------------------------- events

[EventType("seat-reserved")]
public sealed record SeatReserved(
    [property: Tag("show")] Guid ShowId,
    [property: Tag("seat")] string Seat,
    [property: Tag("customer")] Guid CustomerId) : IEvent;

[EventType("reservation-cancelled")]
public sealed record ReservationCancelled(
    [property: Tag("show")] Guid ShowId,
    [property: Tag("seat")] string Seat) : IEvent;

// ---------------------------------------------------------------- the decision

public sealed record ReserveSeat(Guid ShowId, string Seat, Guid CustomerId);

public sealed record SeatState
{
    public bool IsTaken { get; init; }
}

public static class Seat
{
    // The consistency boundary: every event about this one seat at this one show, and nothing
    // else. Two people reserving different seats never contend; two people reserving the same
    // seat always do.
    public static DcbQuery Boundary(Guid showId, string seat) =>
        DcbQuery.ByAllTags(
            new EventTag("show", showId.ToString()),
            new EventTag("seat", seat));

    public static SeatState Apply(SeatState state, IEvent e) => e switch
    {
        SeatReserved => state with { IsTaken = true },
        ReservationCancelled => state with { IsTaken = false },
        _ => state
    };
}

// ---------------------------------------------------------------- the read model

public sealed record ShowOccupancy
{
    public int SeatsTaken { get; init; }
}

public static class OccupancyProjection
{
    public static readonly ProjectionDeclaration<ShowOccupancy> Declaration =
        DeclareProjection.For<ShowOccupancy>("show-occupancy")
            .On<SeatReserved>(
                id: e => e.ShowId.ToString(),
                apply: (state, _, _) => state with { SeatsTaken = state.SeatsTaken + 1 })
            .On<ReservationCancelled>(
                id: e => e.ShowId.ToString(),
                apply: (state, _, _) => state with { SeatsTaken = state.SeatsTaken - 1 })
            .Build();
}

public static class Program
{
    public static async Task Main()
    {
        var occupancy = new InMemoryStateStore<ShowOccupancy>();

        var services = new ServiceCollection();

        services.AddAlberto("tickets", builder => builder
            .WithInMemory()
            .WithEventsFrom(Assembly.GetExecutingAssembly())
            .AddProjection(OccupancyProjection.Declaration, _ => () => occupancy)
            .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(50) }));

        var provider = services.BuildServiceProvider(validateScopes: true);
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        // AlbertoStore is scoped — in a web app that is the request scope. Here we open one by hand.
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredKeyedService<AlbertoStore>("tickets");
        var showId = Guid.NewGuid();

        Task<Result> Reserve(string seat) =>
            store.Handle(new ReserveSeat(showId, seat, Guid.NewGuid()))
                .Load(Seat.Boundary(showId, seat), new SeatState(), Seat.Apply)
                .Decide((cmd, state) => state.IsTaken
                    ? Problem.Create("seat.taken", $"Seat {cmd.Seat} is already reserved.")
                    : Decision.Succeed(new SeatReserved(cmd.ShowId, cmd.Seat, cmd.CustomerId)))
                .Commit(CancellationToken.None);

        var first = await Reserve("A12");
        Console.WriteLine($"A12 first attempt : {(first.IsSuccess ? "reserved" : first.Problems[0].Message)}");

        var second = await Reserve("A12");
        Console.WriteLine($"A12 second attempt: {(second.IsSuccess ? "reserved" : second.Problems[0].Message)}");

        var other = await Reserve("B07");
        Console.WriteLine($"B07              : {(other.IsSuccess ? "reserved" : other.Problems[0].Message)}");

        // Wait for the control loop instead of sleeping for a guess.
        var projections = provider.GetRequiredKeyedService<ProjectionCatchUp>("tickets");
        await projections.WaitForProjectionAsync("show-occupancy");

        var states = await occupancy.LoadManyAsync([showId.ToString()]);
        Console.WriteLine($"seats taken      : {states[showId.ToString()].SeatsTaken}");
    }
}
```

`dotnet run` prints:

```
A12 first attempt : reserved
A12 second attempt: Seat A12 is already reserved.
B07              : reserved
seats taken      : 2
```

Note the wait. Projections are **eventually** consistent — the control loop polls, so a read
immediately after a write may not see it yet. That lag is the price of the decision path not
waiting on every read model.

`WaitForProjectionAsync` is how you read your own writes anyway: it reads the store's head once
and returns as soon as that projection's checkpoint has passed it, throwing `TimeoutException`
rather than quietly serving a stale read. Pass a position to wait for something specific, and a
`TimeSpan` to override the default (five seconds, or three control-loop polls, whichever is
longer). It watches a checkpoint, so it only reports progress made *in this process* — on a
replica that does not run the processor it will wait out its timeout however far along the
projection actually is.

If a particular projection must be readable the instant the mutation returns, without waiting at
all, EF projections can run [inline](projections.md#inline-vs-async) instead.

## Where to go next

- **[Concepts](concepts.md)** — what positions, checkpoints and processors are, and how queries
  really compose.
- **[Projections](projections.md)** — Postgres and EF stores, and rebuilding a projection without
  downtime.
- **[Reactors and the outbox](reactors-and-outbox.md)** — doing things to the outside world.
- **[Operations](operations.md)** — the `alberto` CLI, dead letters, retries and telemetry.
- **A real application** — `apps/Alberto.Orders` is a GraphQL API on Postgres with tenancy,
  EF projections and an outbox. Run the lot with
  `dotnet run --project apps/Alberto.AppHost`.
