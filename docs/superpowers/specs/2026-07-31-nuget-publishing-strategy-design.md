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
   #71 all failed this way; #65 was the last success.
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
| `src/Alberto.Commands` | `Alberto.Commands` | `Alberto` (merged, as today) | yes |
| `src/Alberto.Commands.Analyzers` | — | `Alberto.Commands.Analyzers` | bundled into `Alberto.Commands` |
| `src/Alberto.EntityFramework` | `Alberto.EntityFramework` | `Alberto.EntityFramework` | yes |
| `src/Alberto.InMemory` | `Alberto.InMemory` | `Alberto.InMemory` | yes |
| `src/Alberto.Messaging` | `Alberto.Messaging` | `Alberto.Messaging` | yes |
| `src/Alberto.Postgres` | `Alberto.Postgres` | `Alberto.Postgres` | yes |
| `src/Alberto.Postgres.Messaging` | `Alberto.Postgres.Messaging` | `Alberto.Postgres.Messaging` | yes |
| `src/Alberto.Telemetry` | `Alberto.Telemetry` | `Alberto.Telemetry` | yes |
| `src/Alberto.Testing` | `Alberto.Testing` | `Alberto.Testing` | yes |
| `src/Alberto.Testing.Xunit` | `Alberto.Testing.Xunit` | `Alberto.Testing.Xunit` | yes |
| `src/Alberto.Admin` | reserved, unpublished | `Alberto.Admin` | no — parked |
| `src/Alberto.Postgres.Admin` | reserved, unpublished | `Alberto.Postgres.Admin` | no — parked |
| `tools/Alberto.Cli` | `Alberto.Cli` | `Alberto.Cli` | yes — tool, command `alberto` |

Also renamed: `tests/Alberto.Dcb.Tests` → `tests/Alberto.Tests`,
`benchmarks/Alberto.Dcb.Benchmarks` → `benchmarks/Alberto.Benchmarks`.

`Alberto.Commands` keeps `RootNamespace` set to `Alberto` rather than taking its own
namespace, preserving the deliberate merge the csproj does today: command types live
alongside the core types.

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

### Admin DLLs inside the CLI package

`Alberto.Admin.dll` and `Alberto.Postgres.Admin.dll` are bundled into the `Alberto.Cli` tool
package, because a `PackAsTool` package carries its full dependency closure. This is correct
and is not "an admin package on nuget.org": a tool package cannot be referenced, so the types
inside it are not a semver-frozen public surface. No action needed.

## 2. Package README

`Directory.Build.props` packs the repository root `README.md` into every package. It stays
that way. The `PackageIcon` TODO in that file remains open and does not block v0.1.0.

## 3. Enforcement: an allowlist, not a convention

The pack list is a hand-maintained bash loop and the push is a glob. The CLI bug proves that
packability-by-property fails **silently in both directions** — it dropped a package that was
meant to ship, so it can equally ship one that was not.

A verification step runs after packing and before any push. It compares the set of
`artifacts/*.nupkg` basenames against a literal allowlist of the 11 published IDs and fails on
either an extra or a missing entry:

- an `Alberto.Admin.nupkg` appearing fails the build,
- an absent `Alberto.Cli.nupkg` fails the build.

The allowlist lives in the workflow as an explicit list. Adding a package to it is a
reviewable diff.

## 4. Workflow

Everything stays in `.github/workflows/publish-packages.yml`. The nuget.org trusted-publishing
policy is bound to that filename; a new workflow file would require editing the policy.

**Standing fix, independent of everything else:** `<IsPackable>true</IsPackable>` in
`tools/Alberto.Cli/Alberto.Cli.csproj`.

### Trigger: push to `main`

Unchanged from today. Packs `0.1.0-beta.<run_number>`, pushes to GitHub Packages. The
five-attempt retry loop around the CLI push stays — the `nuget.pkg.github.com` hangs it
documents are real and the workflow's existing comment explains why a longer timeout does not
help.

### Trigger: push tag `v*`

Version derives from the tag (`${TAG#v}`), making the tag the single source of truth. Nothing
reads `VersionPrefix` for a release, so the tag and the props file cannot drift.

The job needs `permissions: id-token: write`. The OIDC exchange happens through
`NuGet/login@v1`, whose `NUGET_API_KEY` output is valid for one hour — so the login step goes
immediately before the push, after packing, not at the top of the job.

`NuGet/login@v1` takes a `user` input: the **nuget.org profile name**, not an email address.
It is added as the repository secret `NUGET_USER`.

`.snupkg` symbol packages push to nuget.org alongside the `.nupkg` files. No retry wrapper —
the flakiness is specific to GitHub Packages.

## 5. Sequencing

Four pull requests. Merging the rename into a pipeline that is already red makes it
impossible to attribute a failure.

1. **Repair and enforce.** `IsPackable=true` on the CLI, plus the §3 allowlist step, on
   today's names. Merge and confirm a green GitHub Packages run. The pipeline becomes
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
