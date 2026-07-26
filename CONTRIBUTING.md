# Contributing to Alberto

Thank you for taking the time to contribute.

## Getting started

Clone the repository and make sure you have the prerequisites installed:

- **.NET 10 SDK** — the solution targets `net10.0`
- **Docker** — Testcontainers-backed integration tests spin up a PostgreSQL container
- **Node.js 20+** — required only for the K6 load tests under `tests/Alberto.Orders.LoadTests/`

Run the full test suite:

```bash
dotnet test
```

Tests whose names start with `Postgres` or that live in a Testcontainers fixture class require a
running Docker daemon. The in-memory and unit tests run without Docker.

## Making changes

- One logical change per pull request.
- All public API additions or removals must be reflected in the project's
  `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` (see the
  `Microsoft.CodeAnalysis.PublicApiAnalyzers` setup in `Directory.Build.props`).
- Breaking changes must have a corresponding entry in `UPGRADING.md` using the existing format
  (summary table, before/after code snippets, migration steps).
- `CHANGELOG.md` entries are added under the `## [Unreleased]` heading.
- `TreatWarningsAsErrors` is on. The build must be warning-free before a PR can be merged.

## Code style

The repo has a root `.editorconfig` and a `src/Alberto.Dcb/.editorconfig`, but both exist
solely to suppress `PublicApiAnalyzers` RS00xx diagnostics while the public-API baselines
are empty — they are not general formatting rules. For code style, follow the surrounding
code. Long explanatory comments are encouraged on non-obvious decisions. Do not add narration
comments on self-evident code.

## Event deserialization rule

**Never turn an `IEventEnvelope` payload into a typed event object by calling
`JsonSerializer.Deserialize`, `JsonDocument.Parse`, or `JsonNode.Parse` directly.
Always route through `EventSerializer.Deserialize`.**

### Why this matters

`EventSerializer.Deserialize` applies the registered upcaster chain before returning the
typed event.  A call to `JsonSerializer.Deserialize` skips that chain entirely, so a
processor handling a v2 event shape silently receives a v1 shape whenever it reads an old
envelope.  The deserialization succeeds — the missing fields are just `null` or default —
so the bug is invisible until it corrupts projection state or triggers a null-reference
crash in a handler that assumed the field was populated.

This defect was introduced three separate times in three different places, each time with
a green test sitting next to it.  The rule is enforced by a source-scanning guard test in
`tests/Alberto.Dcb.Tests/UpcasterBypassGuardTests.cs`.

### How to obtain an EventSerializer

Inject `EventSerializer` from DI.  Both the Postgres and InMemory backends register it as
part of `AddAlberto(...)`:

```csharp
// Constructor injection in a projection, reactor, or outbox mapper:
public MyProcessor(EventSerializer serializer)
{
    _serializer = serializer;
}

// When you have the envelope:
var typedEvent = _serializer.Deserialize(envelope);
```

### Genuine non-event JSON — adding to the allow-list

If you are writing code that deserializes non-event JSON (projection state, metadata
dictionaries, CLI config, outbox payloads) and the file is not already in the guard's
allow-list, add an entry to `AllowedFiles` in `UpcasterBypassGuardTests`:

```csharp
["Alberto.SomeLib/SomeFile.cs"] =
    "Deserializes Dictionary<string,string> metadata column, not an event payload.",
```

The justification **must name the concrete type** (e.g. `Dictionary<string,string>`,
`TState`, `AlbertoConfig`).  "Not an event" alone is not sufficient.

## Submitting a pull request

Open a draft PR early if you want feedback on the direction. Mark it ready when the CI checks pass and you have addressed any self-review items.
