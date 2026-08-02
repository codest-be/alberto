# Contributing to Alberto

Thank you for taking the time to contribute.

## Getting started

Clone the repository and make sure you have the prerequisites installed:

- **.NET 10 SDK.** The solution targets `net10.0`
- **Docker.** Testcontainers-backed integration tests spin up a PostgreSQL container
- **Node.js 20+.** Required only for the K6 load tests under `tests/Alberto.Orders.LoadTests/`

Run the full test suite:

```bash
dotnet test
```

Tests whose names start with `Postgres` or that live in a Testcontainers fixture class require a
running Docker daemon. The in-memory and unit tests run without Docker.

## Making changes

- **Every pull request must close an issue.** Put `Closes #123` in the PR description. This is
  enforced by the `pr-policy` check, and it is not decoration. The issue's **milestone** is what
  decides which version your change ships in. See [Releasing](docs/releasing.md).
- **The linked issue must carry a milestone** naming a real version (`1.1.0`, `1.0.1`). The
  standing `Backlog` milestone is not a valid target; an issue has to be triaged out of it before
  a PR can close it. If your issue is still in `Backlog`, ask for it to be scheduled.
- **Label a PR `breaking-change` if it breaks a consumer**, and describe the migration in the PR
  description: what broke, and the before/after a caller needs. `pr-policy` will reject the label
  against a milestone that cannot legally carry a break. That description becomes the release
  notes, so write it for the person doing the upgrade.
- One logical change per pull request.
- All public API additions or removals must be reflected in the project's
  `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`. See [Public API tracking](#public-api-tracking).
  A non-empty `PublicAPI.Unshipped.txt` is also what tells the release job a minor bump is due.
- **Do not edit `CHANGELOG.md`.** The release workflow drafts each section from the milestone's
  closed issues, and the release PR is where that draft is edited into prose. Hand-editing
  `[Unreleased]` on every PR just produces merge conflicts.
- `TreatWarningsAsErrors` is on. The build must be warning-free before a PR can be merged.

## Public API tracking

Every **packable** project under `src/` carries two tracking files, and
`Microsoft.CodeAnalysis.PublicApiAnalyzers` **fails the build** on a public symbol that appears
in neither. The point is that widening the surface Alberto has to support for the life of a major
version is a line in a diff a reviewer can see, rather than something that lands with a feature.

The gate is conditioned on `IsPackable` (see **Packability** below), so the three non-packable
projects under `src/` are outside it: `Alberto.Admin.Postgres` and `Alberto.Commands.Analyzers`
carry no tracking files at all, and `Alberto.Admin` carries them but is not checked against them.
Its files are there so unparking the admin surface is one property flip rather than a surface
audit. Nothing in those three assemblies is under semver until they are published.

The rules are set to `error` in the root `.editorconfig`. Nothing suppresses them. If you find
yourself adding a `NoWarn`, a `.globalconfig`, or a project-local `.editorconfig` to get a build
green, that is the gate working.

**Adding public API.** Build, then either apply the "Add to public API" fix in the IDE, or run:

```bash
dotnet format analyzers src/Alberto/Alberto.csproj --diagnostics RS0016 --severity error
```

Either way the new entries land in `PublicAPI.Unshipped.txt`. **Read that diff before committing
it.** It is the statement of what you are committing to support. (`dotnet format` does not accept
`Alberto.slnx`; pass the individual `.csproj`.)

**Removing public API.** Delete the entry by hand, label the PR `breaking-change`, and describe
the migration in the PR description. Removing a shipped entry is a **major**-version change. See
[Releasing](docs/releasing.md).

**At release.** Move everything from `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` and
leave `Unshipped` with just its `#nullable enable` line.

**RS0026 / RS0027** flag an added overload with optional parameters. They are evolution
guidelines, not bugs, and are satisfiable by justification when the overloads are separated by a
*required* parameter or by delegate shape rather than by the optional tail. Where that is the
case, add a `[SuppressMessage]` with a `Justification` naming which parameter does the separating.
See the existing ones in `DcbModuleBuilderExtensions.ReactTo` for the form. Do not reach for
`#pragma warning disable`, and do not lower the severity.

**Packability.** `Directory.Build.props` defaults `IsPackable` to `false`; each project under
`src/` opts in with `<IsPackable>true</IsPackable>`. That opt-in is what brings in the package
metadata, SourceLink, and this gate, so a new package needs the property *and* the two tracking
files, and an app, test, or tool project needs neither.

## Extension points

A handful of interfaces are meant to be implemented *outside* this repository: someone writing a
backend for a database Alberto does not ship, or a store that fits their own operational setup.
Public API tracking above records that they widened; it cannot tell you the widening was safe.

The distinction it misses is between the two ways to add an interface member. A member with a
default body is invisible to existing implementors. An **abstract** member breaks every one of
them: at compile time when they rebuild, and at load time for anything already deployed against
the old interface. There is no way to walk that back short of a major version.

So the rule is: **after 1.0, new members on these interfaces ship with a default implementation.**

`ExtensionPointContractTests` in `tests/Alberto.Tests` enforces it by pinning today's abstract
member set on `IEventStoreBackend`, `IDeadLetterStore`, `IClaimableDeadLetterStore` and
`IProjectionRebuildStore`. A member added with a default body does not appear in the reflected
abstract set and the test stays green.

**If that test fails**, in order of preference:

1. **Give the member a default**, in terms of the members that already exist. The test stops
   seeing it.
2. **There is no correct default**, which means the capability is optional, not universal. Put it
   on its own interface and type-test for it at the call site. `IClaimableDeadLetterStore` and
   `IFencedCheckpointStore` are the worked examples: the first exists because atomic
   claim-and-fence is not something every store can offer, and a default that *looked* right would
   have moved the break from compile time to a lost event under contention.
3. **It genuinely has to be required.** Update the baseline in the test, label the PR
   `breaking-change`, and describe the migration in the PR description. That is a major-version
   change; the test exists so you cannot make one by accident.

Interfaces Alberto implements for itself are not in scope here. Widen those freely. If you are
unsure which kind you are looking at, ask whether a third party could plausibly have written it.

`IAdminReader` and `IAdminOperator` are deliberately absent from the baseline. They are consumed
by the parked front doors on `feature/admin-surface` and must stay **additive** so that branch
keeps merging; see the note in `CLAUDE.md`.

## Code style

The repo has a root `.editorconfig`. Its `dotnet_diagnostic` entries configure the public-API
analyzer described above; it is not a general formatting ruleset. For code style, follow the
surrounding code. Long explanatory comments are encouraged on non-obvious decisions. Do not add
narration comments on self-evident code.

## Event deserialization rule

**Never turn an `IEventEnvelope` payload into a typed event object by calling
`JsonSerializer.Deserialize`, `JsonDocument.Parse`, or `JsonNode.Parse` directly.
Always route through `EventSerializer.Deserialize`.**

### Why this matters

`EventSerializer.Deserialize` applies the registered upcaster chain before returning the
typed event.  A call to `JsonSerializer.Deserialize` skips that chain entirely, so a
processor handling a v2 event shape silently receives a v1 shape whenever it reads an old
envelope.  The deserialization succeeds: the missing fields are just `null` or default,
so the bug is invisible until it corrupts projection state or triggers a null-reference
crash in a handler that assumed the field was populated.

This defect was introduced three separate times in three different places, each time with
a green test sitting next to it.  The rule is enforced by a source-scanning guard test in
`tests/Alberto.Tests/UpcasterBypassGuardTests.cs`.

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

### Genuine non-event JSON: adding to the allow-list

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
