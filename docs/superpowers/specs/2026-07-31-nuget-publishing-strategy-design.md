# NuGet publishing strategy

Date: 2026-07-31

## Problem

Alberto has never published to nuget.org. Package IDs, once pushed, are permanent — a
version can be unlisted but never deleted, and an ID can never be reclaimed. The naming
therefore has to be settled before the first push, not after.

Three things are true of the repository today and shape the answer:

1. **The publish workflow has been failing since #67.** `Directory.Build.props` now defaults
   `IsPackable=false` and every project under `src/` opts back in, but
   `tools/Alberto.Cli/Alberto.Cli.csproj` never does — `PackAsTool` does not imply
   `IsPackable`. `dotnet pack` on the CLI exits 0 and writes no file, then
   `dotnet nuget push "artifacts/Alberto.Cli*.nupkg"` fails with
   `File does not exist`, five times, and the job exits 1. Runs for #67, #68, #69, #70 and
   #71 all failed this way; #65 was the last success. Since the CLI is now unpublished
   (see §1), this is repaired by deleting the CLI pack and push steps rather than by
   opting the project back into packing.
2. **The admin surface is already excluded correctly.** `Alberto.Dcb.Admin` and
   `Alberto.Dcb.Postgres.Admin` are `IsPackable=false` and absent from the workflow's pack
   loop. The split of `Alberto.Dcb.Postgres.Admin` out of `Alberto.Dcb.Postgres` removed the
   `ProjectReference` that would otherwise have made Admin a hard package dependency of a
   shipped package. Nothing further is needed to keep admin off nuget.org — only enforcement,
   see §3.
3. **`codest-be/alberto` is private.** Every `Alberto*` ID on nuget.org is unclaimed (all 404
   on the flat container), so the naming space is open.

## Decisions

| Decision | Choice |
|---|---|
| Package IDs | Flatten `Alberto.Dcb.*` → `Alberto.*` |
| Namespaces | Flatten too — IDs, assemblies, directories and namespaces move together |
| Ordering | Feature before implementation: `Alberto.Messaging.Postgres`, not `Alberto.Postgres.Messaging` |
| Published set | 10 libraries. The CLI does not ship |
| Feeds | Both, on **different triggers** |
| First nuget.org version | `0.1.0` |
| Repository visibility | Public, at v0.1.0 |

### Why flatten

`.Dcb.` appears in every package ID, so it disambiguates nothing within the product. It is
the last free moment to remove it: no consumer exists outside the org, so there is no
transition period, no deprecated shim, and no compatibility package to maintain.

The rename goes all the way down rather than stopping at the package ID. A package
`Alberto.Postgres` shipping an assembly `Alberto.Dcb.Postgres.dll` would trade one
inconsistency for another.

### Why feature before implementation

`Alberto.Dcb.Postgres.Messaging` inverts the ordering every comparable package uses. The
repository already establishes the right one: `Alberto.Testing` /
`Alberto.Testing.Xunit` is feature-then-implementation, and .NET at large agrees —
`Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.Extensions.Caching.StackExchangeRedis`,
`Serilog.Sinks.Seq`. The abstraction owns the namespace and providers slot underneath it.

So `Alberto.Messaging.Postgres`, and for the same reason the parked
`Alberto.Postgres.Admin` becomes `Alberto.Admin.Postgres`. The parked one is free — it has
never published and never will under the current plan.

Note this is a rename of the bridge only. `Alberto.Postgres` keeps its name: it is the
Postgres implementation of the core event store, which lives in the root package, so there
is no intermediate feature segment to insert.

### Why `0.1.0` and not `1.0.0`

Semver permits breaking changes in `0.x` minor bumps. The public surface will have existed
under its new namespace layout for zero days at the moment of first publish, and real
consumers surface API problems that internal use does not. `0.1.0` claims the IDs, proves
the pipeline, and preserves room to move. `PublicAPI.Shipped.txt` keeps the surface honest
regardless of the version number.

### Why both feeds, on different triggers

GitHub Packages requires authentication even for public packages, so it cannot serve as a
public distribution channel. It is however a genuinely useful staging ground: pushes there
are deletable, whereas nuget.org pushes are permanent. That difference in reversibility is
the reason the two feeds get different triggers rather than sitting side by side in one job.

A nuget.org release must never be a side effect of merging a pull request.

## 1. Identity map

| Directory | Package ID | Root namespace | Ships |
|---|---|---|---|
| `src/Alberto` | `Alberto` | `Alberto` | yes |
| `src/Alberto.Commands` | `Alberto.Commands` | `Alberto.Commands` (**changed** — see below) | yes |
| `src/Alberto.Commands.Analyzers` | — | `Alberto.Commands.Analyzers` | bundled into `Alberto.Commands` |
| `src/Alberto.EntityFramework` | `Alberto.EntityFramework` | `Alberto.EntityFramework` | yes |
| `src/Alberto.InMemory` | `Alberto.InMemory` | `Alberto.InMemory` | yes |
| `src/Alberto.Messaging` | `Alberto.Messaging` | `Alberto.Messaging` | yes |
| `src/Alberto.Postgres` | `Alberto.Postgres` | `Alberto.Postgres` | yes |
| `src/Alberto.Messaging.Postgres` | `Alberto.Messaging.Postgres` | `Alberto.Messaging.Postgres` (**reordered**) | yes |
| `src/Alberto.Telemetry` | `Alberto.Telemetry` | `Alberto.Telemetry` | yes |
| `src/Alberto.Testing` | `Alberto.Testing` | `Alberto.Testing` | yes |
| `src/Alberto.Testing.Xunit` | `Alberto.Testing.Xunit` | `Alberto.Testing.Xunit` | yes |
| `src/Alberto.Admin` | reserved, unpublished | `Alberto.Admin` | no — parked |
| `src/Alberto.Admin.Postgres` | reserved, unpublished | `Alberto.Admin.Postgres` (**reordered**) | no — parked |
| `tools/Alberto.Cli` | — | `Alberto.Cli` | **no** — see below |

Also renamed: `tests/Alberto.Dcb.Tests` → `tests/Alberto.Tests`,
`benchmarks/Alberto.Dcb.Benchmarks` → `benchmarks/Alberto.Benchmarks`.

The `InternalsVisibleTo` grant in `Alberto.Postgres` that lets the outbox store reach
`SchemaQualifier` follows the assembly rename to `Alberto.Messaging.Postgres`.

### The CLI does not ship

`Alberto.Cli` is excluded from both feeds for v0.1.0, keeping the released surface to ten
libraries. Operators run it from source — `dotnet run --project tools/Alberto.Cli -- status`
— which is viable precisely because the repository goes public at release.

This also repairs the red pipeline by subtraction. The failing push step, its five-attempt
retry loop and the comment explaining why a longer timeout does not help are all deleted
rather than fixed. The `IsPackable=false` default in `Directory.Build.props` then already
describes the CLI correctly, so that file needs no change. The CLI's `dotnet build` step
stays — it is the compile check for the parked admin projects it references.

Shipping it later is two lines: `IsPackable=true` in the csproj, and one entry in the §3
allowlist.

Consequence: nothing published carries the admin assemblies. Previously
`Alberto.Admin.dll` and `Alberto.Admin.Postgres.dll` would have been bundled into the CLI
tool package as part of its dependency closure — correct behaviour, but it is now moot. No
artifact reaching either feed contains admin code at all.

### Un-merging `Alberto.Commands`

Today all nine source files in `Alberto.Dcb.Commands` declare `namespace Alberto.Dcb;` —
the same namespace as the core package — and no consumer anywhere writes
`using Alberto.Dcb.Commands`. This is a real merge, not a `RootNamespace` default.

It ends with the rename. `Alberto.Commands` takes `namespace Alberto.Commands`.

The merge does not carry its weight. The package declares exactly ten public types, all of
them pipeline machinery: `AlbertoStore`, `AlbertoStoreBuilderExtensions`,
`CommandPipeline<TCommand>`, `BoundPipeline<,>`, `UnboundPipeline<,>`, `BoundDecision`,
`BoundDecision<TValue>`, `UnboundDecision`, `UnboundDecision<TValue>` and
`DeciderExtensions`. The types a consumer needs pervasively — `Result`, `Result<T>`,
`Decision`, `Decision<T>`, `Problem` — are declared in the **core** package
(`src/Alberto/Result.cs`, `Decision.cs`, `Problem.cs`) and stay in `namespace Alberto`
either way. So the merge buys no ergonomics on the types that actually appear everywhere.

Against that, once the root namespace is plain `Alberto`, an optional package injecting types
into it is actively misleading: `Alberto` reads as the core package's namespace, and every
other package in the product already has the property that its namespace names its package.

Cost: 21 files gain a `using Alberto.Commands;`, mostly one per vertical slice in the Orders
and Payments examples. `AlbertoStoreBuilderExtensions` holds extension methods, so DI
registration sites need the import too or the methods will not resolve.

### Fix the `Alberto.Commands` description while renaming

`Alberto.Dcb.Commands.csproj` currently describes itself as providing "Problem, Result,
Decision types". It does not — those are core types, as established above. That string
becomes the package summary on nuget.org, so it is corrected as part of the rename to
describe what the package actually contains: the fluent
validate → load → decide → persist pipeline.

The parked admin projects keep their `PackageId`/`Title`/`Description` metadata and their
`IsPackable=false`, exactly as they do now — unparking stays a one-line diff rather than a
rediscovery.

### Scale

440 of 502 `.cs` files reference `Alberto.Dcb`. 22 `PublicAPI.*.txt` files hold 1912
fully-qualified declarations. 41 markdown, yml, props and json files mention the old names.
This is a whole-repository rewrite.

The public-API gate is the verification. Every one of those 1912 declarations must match the
new surface exactly, at error severity, so a rewrite that misses a type or renames one
inconsistently cannot compile. This is why the rename gets a pull request to itself.

## 2. Package README

`Directory.Build.props` packs the repository root `README.md` into every package. It stays
that way. The `PackageIcon` TODO in that file remains open and does not block v0.1.0.

## 3. Enforcement: an allowlist, not a convention

The pack list is a hand-maintained bash loop and the push is a glob. The CLI bug proves that
packability-by-property fails **silently in both directions** — it dropped a package that was
meant to ship, so it can equally ship one that was not.

A verification step runs after packing and before any push. It compares the set of
`artifacts/*.nupkg` basenames against a literal allowlist of the ten published IDs and fails
on either an extra or a missing entry:

- an `Alberto.Admin.nupkg` or `Alberto.Cli.nupkg` appearing fails the build,
- an absent `Alberto.Postgres.nupkg` fails the build.

The ten:

```
Alberto
Alberto.Commands
Alberto.EntityFramework
Alberto.InMemory
Alberto.Messaging
Alberto.Messaging.Postgres
Alberto.Postgres
Alberto.Telemetry
Alberto.Testing
Alberto.Testing.Xunit
```

The allowlist lives in the workflow as an explicit list. Adding a package to it is a
reviewable diff. Note the check must match whole IDs, not prefixes — `Alberto.Messaging`
is a prefix of `Alberto.Messaging.Postgres`, so a substring test would let an unexpected
`Alberto.Messaging.Something.nupkg` through.

## 4. Workflow

Everything stays in `.github/workflows/publish-packages.yml`. The nuget.org trusted-publishing
policy is bound to that filename; a new workflow file would require editing the policy.

**Standing fix, independent of everything else:** delete the CLI pack and push steps. The
CLI `dotnet build` step stays — see the note at the end of this section. That is what
unbreaks the workflow; see "The CLI does not ship" in §1.

### Trigger: push to `main`

Packs `0.1.0-beta.<run_number>` and pushes the ten libraries to GitHub Packages. Otherwise
unchanged.

The observed `nuget.pkg.github.com` hangs were on the CLI push specifically, and that step is
gone. The library push keeps its existing single 600s timeout; if the hangs turn out not to
have been CLI-specific, the retry loop can come back on the library push, where it never
was.

### Trigger: push tag `v*`

Version derives from the tag (`${TAG#v}`), making the tag the single source of truth. Nothing
reads `VersionPrefix` for a release, so the tag and the props file cannot drift.

The job needs `permissions: id-token: write`. The OIDC exchange happens through
`NuGet/login@v1`, whose `NUGET_API_KEY` output is valid for one hour — so the login step goes
immediately before the push, after packing, not at the top of the job.

`NuGet/login@v1` takes a `user` input: the **nuget.org profile name**, not an email address.
It is added as the repository secret `NUGET_USER`.

`.snupkg` symbol packages push to nuget.org alongside the `.nupkg` files. No retry wrapper —
the observed flakiness was on GitHub Packages.

The CLI is still **built** on both triggers even though it never packs — it references the
parked admin projects, so building it is what keeps a compile break in `Alberto.Admin` or
`Alberto.Admin.Postgres` from going unnoticed. This is the same reason the workflow already
gives for building the admin projects it does not pack.

## 5. Sequencing

Four pull requests. Merging the rename into a pipeline that is already red makes it
impossible to attribute a failure.

1. **Repair and enforce.** Delete the CLI pack and push steps (keeping the build), add the
   §3 allowlist step, on today's names. Merge and confirm a green GitHub Packages run. The pipeline becomes
   trustworthy before anything large moves through it.
2. **The rename.** Nothing else in the diff.
3. **Wire nuget.org.** Tag trigger, `id-token: write`, `NuGet/login@v1`, the `NUGET_USER`
   secret.
4. **Release.** Make `codest-be/alberto` public, then push tag `v0.1.0`.

### Why visibility flips at step 4

The packages are MIT-licensed and configured for SourceLink. Published from a private
repository, `PackageProjectUrl` and `RepositoryUrl` would 404 for every visitor to the
package page, and the `.snupkg` symbols would be undebuggable by anyone outside the org —
the exact investment `Directory.Build.props` already makes would be wasted. Going public at
release makes the existing configuration work as intended.

### Trusted publishing activation

The policy currently shows "use within 7 days to keep it permanently active". This is the
documented behaviour for policies on private repositories: nuget.org cannot capture the
GitHub repository and owner IDs — which lock the policy against repo-resurrection attacks —
until a real publish supplies them in the short-lived token. The first successful push makes
the policy permanently active. If the window lapses first, restarting it is one click and
can be done any number of times, so it does not constrain the sequencing above.

### Prefix reservation

Requested after the first successful publish. "Alberto" is a common given name, so NuGet may
scope or decline a reservation of the `Alberto.` prefix. Nothing breaks if it is declined —
the packages simply do not carry the verified-owner check.

### Orphaned GitHub Packages

The existing `Alberto.Dcb.*` packages on GitHub Packages become orphans after step 2. They
may be deleted or left; nothing depends on them.

## Out of scope

- `feature/admin-surface` will need a rebase rather than a merge after step 2. The CLAUDE.md
  guidance to keep `IAdminReader`/`IAdminOperator` additive was written to protect that merge,
  and a repository-wide namespace rewrite overtakes it. Reconciling that branch is its own
  piece of work.
- The `PackageIcon` TODO in `Directory.Build.props`.
- Publishing the parked admin front doors.
