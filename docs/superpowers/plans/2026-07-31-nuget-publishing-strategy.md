# NuGet Publishing Strategy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship ten `Alberto.*` libraries to nuget.org at v0.1.0 from a public repository, with the admin surface and the CLI provably excluded.

**Architecture:** Four independently mergeable PRs, in order. PR 1 repairs the currently-failing publish workflow and adds an allowlist that fails the job if the packed set ever drifts from ten known IDs. PR 2 is the whole-repo rename (`Alberto.Dcb.*` → `Alberto.*`) and nothing else — package IDs, assemblies, namespaces, directories and embedded-resource names all move together. PR 3 adds a `v*` tag trigger and the nuget.org trusted-publishing push to the same workflow file the policy is bound to. PR 4 releases: promote the public-API surface to Shipped, make the repo public, tag `v0.1.0`.

**Tech Stack:** .NET 10.0, GitHub Actions, `NuGet/login@v1` (OIDC trusted publishing), DbUp-PostgreSQL 7.0.1, `Microsoft.CodeAnalysis.PublicApiAnalyzers`, `Microsoft.SourceLink.GitHub`.

## Global Constraints

- The trusted-publishing policy is bound to the workflow **filename** `publish-packages.yml`. Every nuget.org push must live in that file. Renaming or splitting it invalidates the policy.
- The published set is exactly these ten IDs, and nothing else may ever reach a feed:
  `Alberto`, `Alberto.Commands`, `Alberto.EntityFramework`, `Alberto.InMemory`, `Alberto.Messaging`, `Alberto.Messaging.Postgres`, `Alberto.Postgres`, `Alberto.Telemetry`, `Alberto.Testing`, `Alberto.Testing.Xunit`.
- `Alberto.Admin`, `Alberto.Admin.Postgres`, `Alberto.Commands.Analyzers` and `tools/Alberto.Cli` stay `IsPackable=false`. The two admin projects and the analyzer are still **built** in CI — only packing and pushing are removed.
- Segment order is feature before implementation: `Alberto.Messaging.Postgres`, never `Alberto.Postgres.Messaging`.
- `Alberto.Commands` keeps its own namespace `Alberto.Commands`. It is **not** merged into `Alberto`.
- First nuget.org version is `0.1.0`. `VersionPrefix` in `Directory.Build.props` is the single source of truth; a release tag that disagrees with it fails the build.
- nuget.org pushes are permanent. A package can be unlisted but never deleted, and an ID is never reclaimable.
- Rename scope excludes `docs/superpowers/**` and `CHANGELOG.md`. Those are historical records of work done under the old names; rewriting them falsifies the record.
- `Directory.Build.props` needs no change. It already packs the root `README.md` into every package and already defaults `IsPackable=false`; its open `PackageIcon` TODO stays open and does not block v0.1.0.
- The parked admin projects keep their `IsPackable=false` and keep having a `PackageId` / `Title` / `Description` at all, so unparking stays a one-line diff. Those three fields are **not** exempt from the rename: the mechanical pass rewrites `Alberto.Dcb.Admin` → `Alberto.Admin` inside them like anywhere else. What must not happen is the fields being deleted, reworded, or given new `IsPackable` values.
- The string `Alberto.Dcb` is never followed by a letter anywhere in the repo (verified). Type names that merely contain `Dcb` — `DcbQuery`, `DcbModuleBuilder`, `DcbModuleBuilderExtensions` — must **not** change.
- "DCB" as prose (the pattern name, e.g. "Alberto DCB event store") stays. Only the dotted identifier `Alberto.Dcb` moves.

---

## File Structure

Directories renamed in PR 2 (18 total, all via `git mv`):

| Today | After |
|---|---|
| `src/Alberto.Dcb` | `src/Alberto` |
| `src/Alberto.Dcb.Commands` | `src/Alberto.Commands` |
| `src/Alberto.Dcb.Commands.Analyzers` | `src/Alberto.Commands.Analyzers` |
| `src/Alberto.Dcb.EntityFramework` | `src/Alberto.EntityFramework` |
| `src/Alberto.Dcb.InMemory` | `src/Alberto.InMemory` |
| `src/Alberto.Dcb.Messaging` | `src/Alberto.Messaging` |
| `src/Alberto.Dcb.Postgres.Messaging` | `src/Alberto.Messaging.Postgres` |
| `src/Alberto.Dcb.Postgres` | `src/Alberto.Postgres` |
| `src/Alberto.Dcb.Telemetry` | `src/Alberto.Telemetry` |
| `src/Alberto.Dcb.Testing` | `src/Alberto.Testing` |
| `src/Alberto.Dcb.Testing.Xunit` | `src/Alberto.Testing.Xunit` |
| `src/Alberto.Dcb.Admin` | `src/Alberto.Admin` (parked) |
| `src/Alberto.Dcb.Postgres.Admin` | `src/Alberto.Admin.Postgres` (parked) |
| `tests/Alberto.Dcb.Tests` | `tests/Alberto.Tests` |
| `tests/Alberto.Dcb.Tests.SampleEvents` | `tests/Alberto.Tests.SampleEvents` |
| `benchmarks/Alberto.Dcb.Benchmarks` | `benchmarks/Alberto.Benchmarks` |
| `benchmarks/Alberto.Dcb.Benchmarks.Core` | `benchmarks/Alberto.Benchmarks.Core` |
| `benchmarks/Alberto.Dcb.Benchmarks.Compare` | `benchmarks/Alberto.Benchmarks.Compare` |

Files edited outside the mechanical pass:

- `.github/workflows/publish-packages.yml` — CLI steps deleted (Task 1), allowlist added (Task 2), tag trigger + nuget.org push added (Task 7).
- `src/Alberto.Dcb.Commands/**/*.cs` + its `PublicAPI.Unshipped.txt` + 20 call sites — namespace un-merge (Task 3).
- `src/Alberto.Dcb.Commands/Alberto.Dcb.Commands.csproj` — the `Description` is factually wrong and becomes the nuget.org summary (Task 3).
- `CHANGELOG.md` — a hand-written rename entry (Task 6).
- The ten packable projects' `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` — promotion (Task 8). The parked projects' files are left frozen-free.

---

## Known Breaking Consequence: the DbUp migration journal

`PostgresMigrator` loads migration scripts as **embedded resources** and DbUp records each executed script in `schemaversions` **by its resource name**:

```
Alberto.Dcb.Postgres.Migrations.SingleTenant.001_InitialSchema.sql
```

Renaming the assembly changes every one of those names to `Alberto.Postgres.Migrations.…`. DbUp will then consider all 34 scripts pending and try to replay them. The scripts are **not** idempotent (most `CREATE`/`ALTER` statements lack `IF NOT EXISTS`), so a replay against an already-migrated database fails loudly rather than corrupting it.

This is accepted, not mitigated. Nothing is published, no external consumer exists (`PennyForThought` does not reference Alberto), Testcontainers tests build a fresh database per run, and the only persistent store is the local Aspire dev volume. Task 6 drops that volume. The consequence is recorded in `CHANGELOG.md` so it is never mistaken for a bug.

---

# PR 1 — Repair and enforce

## Task 1: Stop packing and pushing the CLI

The CLI has not packed since #67 added `IsPackable=false` as the global default in `Directory.Build.props`. `tools/Alberto.Cli/Alberto.Cli.csproj` never opted back in — `PackAsTool=true` does not imply `IsPackable=true` — so `dotnet pack` exits 0 and writes nothing, and the push step then fails on a missing file. Runs #67, #68, #69, #70 and #71 all died this way.

The CLI is not shipping, so this is repaired by subtraction: delete the pack and the push. No csproj change — the inherited `IsPackable=false` now describes the CLI correctly, and shipping it later stays a two-line diff (`IsPackable=true` plus one allowlist entry).

**Files:**
- Modify: `.github/workflows/publish-packages.yml:66` (keep), `:71-77`, `:87-110`

**Interfaces:**
- Produces: an `artifacts/` directory containing exactly ten `.nupkg` files and their `.snupkg` siblings. Task 2 asserts on it.

- [ ] **Step 1: Reproduce the failure**

```bash
dotnet pack tools/Alberto.Cli/Alberto.Cli.csproj -c Release -o /tmp/cli-pack; echo "EXIT=$?"; ls /tmp/cli-pack 2>&1
```

Expected: `EXIT=0` and `ls` reports no such file or an empty directory. This is the bug — a successful pack that produces nothing.

- [ ] **Step 2: Delete the CLI pack step**

In `.github/workflows/publish-packages.yml`, delete line 77 only:

```yaml
          dotnet pack tools/Alberto.Cli/Alberto.Cli.csproj -c Release --no-build --version-suffix "$VERSION_SUFFIX" -o artifacts
```

Leave the `for proj in …` loop above it untouched. Leave line 66 (`dotnet build tools/Alberto.Cli/Alberto.Cli.csproj -c Release`) untouched — building the CLI is what compile-checks the two parked admin projects it references.

- [ ] **Step 3: Delete the CLI push step and its retry loop**

Delete lines 90-110 in full — the comment block starting `# The CLI push hangs against nuget.pkg.github.com…` through `exit 1`. The retry loop and the comment explaining why a longer timeout does not help existed only for this push and go with it.

- [ ] **Step 4: Fix the symbols step comment**

The comment on lines 112-113 explains itself in terms of the now-deleted CLI push. Replace lines 112-116 with:

```yaml
      # Symbols are best-effort: a failed .snupkg push must not fail a run whose packages
      # already landed. `if: always()` keeps this reachable after an earlier step fails, and
      # `|| true` keeps the step itself green.
      - name: Push symbols to GitHub Packages
        if: always()
        run: dotnet nuget push "artifacts/*.snupkg" --source github --skip-duplicate --timeout 600 || true
```

- [ ] **Step 5: Verify the CLI still builds**

The build step is the compile check for the two parked admin projects the CLI references, so it must survive.

```bash
dotnet build tools/Alberto.Cli/Alberto.Cli.csproj -c Release
```

Expected: `Build succeeded`.

```bash
grep -c "Alberto.Cli" .github/workflows/publish-packages.yml
```

Expected: `1` — the `dotnet build` line, and nothing else.

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/publish-packages.yml && git commit -m "fix: stop packing and pushing the CLI, which has produced no package since #67"
```

---

## Task 2: Assert the packed set

`IsPackable=false` keeps the admin projects out of `artifacts/`, but nothing *checks*. A future `IsPackable=true` plus a line in the pack loop would push an admin package to a permanent feed with no gate in between. This step compares the packed IDs against a literal allowlist and fails the job on any difference in either direction — an unexpected package, or a missing one.

The `ci.yml` "Pack smoke" step already catches the inverse failure (a packable project omitted from the loop, producing NU1101 for consumers). This catches extras. The two are complementary.

**Files:**
- Modify: `.github/workflows/publish-packages.yml` — new step between `Pack` and `Add GitHub Packages source`

**Interfaces:**
- Consumes: `artifacts/*.nupkg` from Task 1.
- Produces: a hard gate that every later push step sits behind.

- [ ] **Step 1: Add the verification step**

Insert directly after the `Pack` step (after old line 77, now the end of the pack block) and before `- name: Add GitHub Packages source`:

```yaml
      # The only thing standing between an accidental IsPackable=true and a permanent
      # nuget.org listing. Compares packed IDs against a literal allowlist and fails on any
      # difference in either direction: an unexpected package, or one that stopped building.
      # The comparison is whole-ID, not prefix: `Alberto.Messaging` is a prefix of
      # `Alberto.Messaging.Postgres`, so a substring test would admit an unexpected
      # `Alberto.Messaging.Something`. Sorted `diff` cannot.
      # `artifacts/*.nupkg` does not match `.snupkg` — that suffix has no dot before "nupkg".
      - name: Verify packed set
        run: |
          cat > /tmp/expected-packages.txt <<'IDS'
          Alberto.Dcb
          Alberto.Dcb.Commands
          Alberto.Dcb.EntityFramework
          Alberto.Dcb.InMemory
          Alberto.Dcb.Messaging
          Alberto.Dcb.Postgres
          Alberto.Dcb.Postgres.Messaging
          Alberto.Dcb.Telemetry
          Alberto.Dcb.Testing
          Alberto.Dcb.Testing.Xunit
          IDS
          LC_ALL=C sort -o /tmp/expected-packages.txt /tmp/expected-packages.txt

          # Strip a trailing semver and the extension to recover the package ID. No Alberto
          # package ID ends in a numeric segment, so this is unambiguous.
          ls artifacts/*.nupkg \
            | xargs -n1 basename \
            | sed -E 's/\.[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\.nupkg$//' \
            | LC_ALL=C sort -u > /tmp/actual-packages.txt

          echo "Packed:"; cat /tmp/actual-packages.txt

          if ! diff -u /tmp/expected-packages.txt /tmp/actual-packages.txt; then
            echo "::error::Packed set does not match the allowlist. '-' lines are missing packages, '+' lines are packages that must never be published."
            exit 1
          fi
```

- [ ] **Step 2: Verify the check locally against a real pack**

```bash
rm -rf /tmp/allowlist-check && for proj in Alberto.Dcb Alberto.Dcb.Commands Alberto.Dcb.EntityFramework Alberto.Dcb.InMemory Alberto.Dcb.Messaging Alberto.Dcb.Postgres Alberto.Dcb.Postgres.Messaging Alberto.Dcb.Telemetry Alberto.Dcb.Testing Alberto.Dcb.Testing.Xunit; do dotnet pack "src/$proj/$proj.csproj" -c Release --version-suffix beta.0 -o /tmp/allowlist-check -v quiet --nologo; done && ls /tmp/allowlist-check/*.nupkg | xargs -n1 basename | sed -E 's/\.[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\.nupkg$//' | LC_ALL=C sort -u
```

Expected: exactly the ten IDs from the allowlist, one per line, in that order.

- [ ] **Step 3: Prove the check fails on an extra package**

```bash
cp /tmp/allowlist-check/Alberto.Dcb.0.1.0-beta.0.nupkg /tmp/allowlist-check/Alberto.Dcb.Admin.0.1.0-beta.0.nupkg && ls /tmp/allowlist-check/*.nupkg | xargs -n1 basename | sed -E 's/\.[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\.nupkg$//' | LC_ALL=C sort -u | diff -u - <(printf 'Alberto.Dcb\nAlberto.Dcb.Commands\nAlberto.Dcb.EntityFramework\nAlberto.Dcb.InMemory\nAlberto.Dcb.Messaging\nAlberto.Dcb.Postgres\nAlberto.Dcb.Postgres.Messaging\nAlberto.Dcb.Telemetry\nAlberto.Dcb.Testing\nAlberto.Dcb.Testing.Xunit\n'); echo "diff exit=$?"
```

Expected: a diff showing `-Alberto.Dcb.Admin` and `diff exit=1`. Then clean up:

```bash
rm -rf /tmp/allowlist-check
```

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/publish-packages.yml && git commit -m "ci: fail the publish job if the packed set drifts from the allowlist"
```

**Merge PR 1 before starting PR 2.** Confirm the next `publish-packages` run on main goes green — it will be the first success since #65.

---

# PR 2 — The rename

Nothing else belongs in this PR. It is 5,122 occurrences across 516 files; a reviewer can only check it by confirming it is purely mechanical.

The real verification is the public-API gate: 1,912 fully-qualified declarations across 22 `PublicAPI.*.txt` files are checked at error severity, so a rewrite that misses a type or renames one inconsistently cannot compile.

`feature/admin-surface` will need a rebase rather than a merge after this PR. Reconciling that branch is out of scope here — but do not attempt to merge it after PR 2 lands without reading the rename commit first.

## Task 3: Un-merge the Commands namespace

`src/Alberto.Dcb.Commands` currently declares `namespace Alberto.Dcb;` — the same namespace as the core library. That merge is not wanted: `Result`, `Result<T>`, `Decision`, `Decision<T>` and `Problem` live in **core**, so they stay reachable from `namespace Alberto` no matter what Commands does. Commands only contributes ten pipeline types, and those get their own namespace.

Doing this first, while names are still unambiguous, means Task 5 can be a single unconditional replace with no exceptions.

**Files:**
- Modify: all 9 `.cs` files under `src/Alberto.Dcb.Commands/`
- Modify: `src/Alberto.Dcb.Commands/Alberto.Dcb.Commands.csproj`
- Modify: `src/Alberto.Dcb.Commands/PublicAPI.Unshipped.txt`
- Modify: `src/Alberto.Dcb.Commands.Analyzers/DiscardedPipelineAnalyzer.cs:62,64`
- Modify: 20 call-site files listed in Step 4

**Interfaces:**
- Produces: namespace `Alberto.Commands` owning exactly `AlbertoStore`, `AlbertoStoreBuilderExtensions`, `BoundDecision`, `BoundDecision<TValue>`, `BoundPipeline<TCommand, TState>`, `CommandPipeline<TCommand>`, `DeciderExtensions`, `UnboundDecision`, `UnboundDecision<TValue>`, `UnboundPipeline<TCommand, TState>`.
- Consumers reach the extension methods `AlbertoStoreBuilderExtensions.WithEventsFrom` and `DeciderExtensions.DecideAndAppendAsync` only via `using Alberto.Commands;` — extension methods are invisible to a type-name grep, which is why the call-site list in Step 4 includes files that never name a Commands type.

- [ ] **Step 1: Move the namespace declaration**

```bash
grep -rl 'namespace Alberto\.Dcb;' src/Alberto.Dcb.Commands --include='*.cs' | xargs perl -pi -e 's/^namespace Alberto\.Dcb;$/namespace Alberto.Commands;/'
```

Verify all nine moved:

```bash
grep -rc 'namespace Alberto\.Commands;' src/Alberto.Dcb.Commands --include='*.cs' | grep -c ':1'
```

Expected: `9`

- [ ] **Step 2: Set the project's RootNamespace**

In `src/Alberto.Dcb.Commands/Alberto.Dcb.Commands.csproj`, change:

```xml
    <RootNamespace>Alberto.Dcb</RootNamespace>
```

to:

```xml
    <RootNamespace>Alberto.Commands</RootNamespace>
```

- [ ] **Step 3: Requalify every fully-qualified reference to the ten types**

This covers `PublicAPI.Unshipped.txt` (where the declarations live) and the analyzer's hardcoded type-name strings in one pass. `AlbertoStoreBuilderExtensions` is listed before `AlbertoStore` because ERE alternation is first-match, not longest-match.

```bash
git ls-files -- '*.cs' 'PublicAPI.*.txt' | xargs perl -pi -e 's/\bAlberto\.Dcb\.(AlbertoStoreBuilderExtensions|AlbertoStore|BoundDecision|BoundPipeline|CommandPipeline|DeciderExtensions|UnboundDecision|UnboundPipeline)\b/Alberto.Commands.$1/g'
```

Verify the analyzer's strings moved:

```bash
grep -n 'Alberto\.' src/Alberto.Dcb.Commands.Analyzers/DiscardedPipelineAnalyzer.cs
```

Expected: `"Alberto.Dcb.Correctness"` (the diagnostic category, untouched here — Task 5 strips it) plus `"Alberto.Commands.BoundDecision"` and `"Alberto.Commands.UnboundDecision"`.

- [ ] **Step 4: Add the using to the 20 call sites**

```bash
for f in \
  apps/Alberto.Orders/Alberto.Orders/Features/AddOrderItem/AddOrderItem.cs \
  apps/Alberto.Orders/Alberto.Orders/Features/CancelOrder/CancelOrder.cs \
  apps/Alberto.Orders/Alberto.Orders/Features/ConfirmOrder/ConfirmOrder.cs \
  apps/Alberto.Orders/Alberto.Orders/Features/CreateOrder/CreateOrder.cs \
  apps/Alberto.Orders/Alberto.Orders/Features/DeliverOrder/DeliverOrder.cs \
  apps/Alberto.Orders/Alberto.Orders/Features/RemoveOrderItem/RemoveOrderItem.cs \
  apps/Alberto.Orders/Alberto.Orders/Features/ShipOrder/ShipOrder.cs \
  apps/Alberto.Orders/Alberto.Orders/Platform/OrdersModule.cs \
  apps/Alberto.Payments/Alberto.Payments/Features/AuthorizePayment/AuthorizePayment.cs \
  apps/Alberto.Payments/Alberto.Payments/Features/CapturePayment/CapturePayment.cs \
  apps/Alberto.Payments/Alberto.Payments/Features/FailPayment/FailPayment.cs \
  apps/Alberto.Payments/Alberto.Payments/Features/InitiatePayment/InitiatePayment.cs \
  apps/Alberto.Payments/Alberto.Payments/Features/RefundPayment/RefundPayment.cs \
  apps/Alberto.Payments/Alberto.Payments/Platform/PaymentsModule.cs \
  tests/Alberto.Dcb.Tests/Analyzers/DiscardedPipelineAnalyzerTests.cs \
  tests/Alberto.Dcb.Tests/CommandPipelineTests.cs \
  tests/Alberto.Dcb.Tests/EvolverUpcastingTests.cs \
  tests/Alberto.Dcb.Tests/UpcasterConfigurationPathTests.cs \
  tests/Alberto.Dcb.Tests/UpcasterPublicApiReviewTests.cs \
  tests/Alberto.Dcb.Tests/UpcasterRegistrationTests.cs \
; do
  grep -q '^using Alberto\.Commands;' "$f" || perl -pi -e 'print "using Alberto.Commands;\n" if $. == 1 && !$done++' "$f"
done
```

Then sort each file's using block if the repo convention requires it — check one file and match what you see:

```bash
head -8 apps/Alberto.Orders/Alberto.Orders/Features/CreateOrder/CreateOrder.cs
```

- [ ] **Step 5: Correct the Commands package description**

`Alberto.Dcb.Commands.csproj` claims to provide "Problem, Result, Decision types". It does not — those are core types, which is the whole reason this task exists. That string becomes the package summary on nuget.org, so it gets fixed here rather than shipping a lie. Replace the `<Description>` element with:

```xml
    <Description>Fluent command pipeline for Alberto DCB. Composes validate → load → decide → persist workflows over a dynamic consistency boundary, with automatic retry on append conflicts.</Description>
```

- [ ] **Step 6: Build and let the compiler find what the list missed**

```bash
dotnet build tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -30
```

Expected: `Build succeeded`. If instead you see `CS0246: The type or namespace name 'CommandPipeline' could not be found` or `CS1061: … does not contain a definition for 'WithEventsFrom'`, add `using Alberto.Commands;` to the named file and rebuild. `DiscardedPipelineAnalyzerTests.cs` embeds C# source as string literals — a missing using there shows up as a failing analyzer test, not a compile error, so the using may need to go *inside* the literal.

- [ ] **Step 7: Build the examples and the benchmarks**

```bash
dotnet build tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -20
```

Expected: `Build succeeded`.

```bash
dotnet build benchmarks/Alberto.Dcb.Benchmarks/Alberto.Dcb.Benchmarks.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -20
```

Expected: `Build succeeded`.

- [ ] **Step 8: Run the tests that cover the pipeline and the analyzer**

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~CommandPipeline|FullyQualifiedName~DiscardedPipelineAnalyzer|FullyQualifiedName~Upcaster"
```

Expected: all pass, zero failures.

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "refactor: give Alberto.Commands its own namespace instead of merging into the core one"
```

---

## Task 4: Reorder the two bridge projects feature-first

`Alberto.Dcb.Postgres.Messaging` reads as "the messaging part of Postgres". It is the reverse: a PostgreSQL implementation of the messaging outbox. Every other pair in the repo is already feature-then-implementation (`Alberto.Dcb.Testing` / `Alberto.Dcb.Testing.Xunit`), and so is the convention in .NET generally (`Microsoft.EntityFrameworkCore.SqlServer`, `Serilog.Sinks.Seq`).

Done here, still under the `Alberto.Dcb.` prefix, so Task 5's replace has no special cases.

**Files:**
- Rename: `src/Alberto.Dcb.Postgres.Messaging/` → `src/Alberto.Dcb.Messaging.Postgres/`
- Rename: `src/Alberto.Dcb.Postgres.Admin/` → `src/Alberto.Dcb.Admin.Postgres/`
- Modify: every file referencing either name

**Interfaces:**
- Produces: package ID `Alberto.Dcb.Messaging.Postgres` (becomes `Alberto.Messaging.Postgres` in Task 5). `Alberto.Dcb.Admin.Postgres` stays `IsPackable=false`.
- `src/Alberto.Dcb.Admin.Postgres` keeps `<RootNamespace>Alberto.Dcb.Postgres</RootNamespace>` — its types deliberately live in the Postgres namespace and the directory name never drove that.

- [ ] **Step 1: Rewrite the text, longest name first**

```bash
git ls-files -- ':(exclude)docs/superpowers/' ':(exclude)CHANGELOG.md' \
  | xargs perl -pi -e 's/Alberto\.Dcb\.Postgres\.Messaging/Alberto.Dcb.Messaging.Postgres/g; s/Alberto\.Dcb\.Postgres\.Admin/Alberto.Dcb.Admin.Postgres/g'
```

Both patterns are literal and longer than `Alberto.Dcb.Postgres`, so plain `Alberto.Dcb.Postgres` references are untouched. Confirm:

```bash
git grep -c "Alberto\.Dcb\.Postgres\.Messaging\|Alberto\.Dcb\.Postgres\.Admin" -- ':(exclude)docs/superpowers/' ':(exclude)CHANGELOG.md'
```

Expected: no output.

- [ ] **Step 2: Move the directories and their project files**

```bash
git mv src/Alberto.Dcb.Postgres.Messaging src/Alberto.Dcb.Messaging.Postgres
git mv src/Alberto.Dcb.Messaging.Postgres/Alberto.Dcb.Postgres.Messaging.csproj src/Alberto.Dcb.Messaging.Postgres/Alberto.Dcb.Messaging.Postgres.csproj
git mv src/Alberto.Dcb.Postgres.Admin src/Alberto.Dcb.Admin.Postgres
git mv src/Alberto.Dcb.Admin.Postgres/Alberto.Dcb.Postgres.Admin.csproj src/Alberto.Dcb.Admin.Postgres/Alberto.Dcb.Admin.Postgres.csproj
```

- [ ] **Step 3: Fix the reordered project's namespace**

`src/Alberto.Dcb.Messaging.Postgres/Alberto.Dcb.Messaging.Postgres.csproj` and its one source file `PostgresOutboxStore.cs` were rewritten by Step 1, so `RootNamespace` and `namespace` already read `Alberto.Dcb.Messaging.Postgres`. Confirm:

```bash
grep -n "RootNamespace\|PackageId" src/Alberto.Dcb.Messaging.Postgres/Alberto.Dcb.Messaging.Postgres.csproj && grep -n "^namespace" src/Alberto.Dcb.Messaging.Postgres/PostgresOutboxStore.cs
```

Expected: `<RootNamespace>Alberto.Dcb.Messaging.Postgres</RootNamespace>`, `<PackageId>Alberto.Dcb.Messaging.Postgres</PackageId>`, `namespace Alberto.Dcb.Messaging.Postgres;`

And confirm the parked admin project kept its Postgres namespace:

```bash
grep -n "RootNamespace" src/Alberto.Dcb.Admin.Postgres/Alberto.Dcb.Admin.Postgres.csproj
```

Expected: `<RootNamespace>Alberto.Dcb.Postgres</RootNamespace>` — unchanged, because Step 1's patterns did not match it, and it becomes `Alberto.Postgres` in Task 5.

The spec's identity map lists this project's root namespace as `Alberto.Admin.Postgres`. Keep `Alberto.Postgres` instead: the merge into the Postgres namespace is deliberate — it puts `AddAlbertoPostgresAdmin` beside the other Postgres registration extensions — and the spec's later, more specific instruction is that the parked projects keep their metadata exactly as it is so unparking stays a one-line diff. Moving a parked project's namespace would also do the most possible damage to `feature/admin-surface`.

- [ ] **Step 4: Verify the `InternalsVisibleTo` grants followed the assembly names**

`src/Alberto.Dcb.Postgres/Alberto.Dcb.Postgres.csproj` grants internals access to three assemblies by name, one of which just moved.

```bash
grep -n "InternalsVisibleTo" src/Alberto.Dcb.Postgres/Alberto.Dcb.Postgres.csproj
```

Expected: `Alberto.Dcb.Tests`, `Alberto.Dcb.Messaging.Postgres` (reordered by Step 1), `Alberto.Dcb.Admin.Postgres` (reordered by Step 1). If any still reads `.Postgres.Messaging` or `.Postgres.Admin`, Step 1 missed the file — rerun it.

- [ ] **Step 5: Update the workflow pack and allowlist entries**

Step 1 rewrote `.github/workflows/publish-packages.yml` and `.github/workflows/ci.yml` too, but the pack loops iterate directory *and* project names in one variable, so verify by eye:

```bash
grep -n "Messaging.Postgres\|Admin.Postgres" .github/workflows/*.yml
```

Expected: `Alberto.Dcb.Messaging.Postgres` in `publish-packages.yml`'s pack loop, its allowlist heredoc, and `ci.yml`'s pack smoke loop. Fix the allowlist's sort order — `Alberto.Dcb.Messaging.Postgres` must now sit directly after `Alberto.Dcb.Messaging`, before `Alberto.Dcb.Postgres`:

```yaml
          Alberto.Dcb
          Alberto.Dcb.Commands
          Alberto.Dcb.EntityFramework
          Alberto.Dcb.InMemory
          Alberto.Dcb.Messaging
          Alberto.Dcb.Messaging.Postgres
          Alberto.Dcb.Postgres
          Alberto.Dcb.Telemetry
          Alberto.Dcb.Testing
          Alberto.Dcb.Testing.Xunit
```

- [ ] **Step 6: Build and test**

```bash
dotnet build tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -20
```

Expected: `Build succeeded`.

```bash
dotnet build tools/Alberto.Cli/Alberto.Cli.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -20
```

Expected: `Build succeeded` — this is what compile-checks both parked admin projects.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "refactor: order the bridge packages feature-first, matching Testing/Testing.Xunit"
```

---

## Task 5: Strip `Alberto.Dcb` to `Alberto`

One unconditional replace, then 16 directory moves. No exceptions remain: the Commands namespace moved in Task 3, the bridge ordering moved in Task 4, and `Alberto.Dcb` is never followed by a letter, so `DcbQuery` and `DcbModuleBuilder` cannot be caught.

**Files:**
- Modify: all 516 tracked files containing `Alberto.Dcb`, excluding `docs/superpowers/**` and `CHANGELOG.md`
- Rename: the 16 remaining directories from the File Structure table

**Interfaces:**
- Produces: the ten final package IDs, the assemblies and namespaces to match, and embedded migration resources under `Alberto.Postgres.Migrations.*`.

- [ ] **Step 1: Record the before-count**

```bash
git grep -o "Alberto\.Dcb" -- ':(exclude)docs/superpowers/' ':(exclude)CHANGELOG.md' | wc -l
```

Note the number. It is the count Step 5 must drive to zero.

- [ ] **Step 2: Replace the text**

```bash
git ls-files -- ':(exclude)docs/superpowers/' ':(exclude)CHANGELOG.md' \
  | xargs perl -pi -e 's/Alberto\.Dcb/Alberto/g'
```

- [ ] **Step 3: Move the directories**

```bash
for d in $(git ls-files | grep -oE '^[a-z]+/Alberto\.Dcb[^/]*' | sort -u); do
  git mv "$d" "$(echo "$d" | sed 's/Alberto\.Dcb/Alberto/')"
done
```

- [ ] **Step 4: Move the project files**

```bash
for f in $(git ls-files | grep 'Alberto\.Dcb'); do
  git mv "$f" "$(echo "$f" | sed 's/Alberto\.Dcb/Alberto/g')"
done
```

- [ ] **Step 5: Verify nothing is left**

```bash
git grep -c "Alberto\.Dcb" -- ':(exclude)docs/superpowers/' ':(exclude)CHANGELOG.md'; echo "grep exit=$?"
```

Expected: no output and `grep exit=1`.

```bash
git ls-files | grep -c 'Alberto\.Dcb'; echo "grep exit=$?"
```

Expected: `0` and `grep exit=1`.

- [ ] **Step 6: Spot-check the four things a blind replace gets subtly wrong**

```bash
grep -n 'const string Name' src/Alberto/Telemetry/AlbertoMetrics.cs
```

Expected: `public const string Name = "Alberto";` — the OpenTelemetry meter and ActivitySource name. This is an **observable change**: any dashboard or collector filtering on the meter name `Alberto.Dcb` must be updated. It is correct for the name to track the package, and there are no published consumers.

```bash
grep -n 'prefix = ' src/Alberto.Postgres/PostgresMigrator.cs
```

Expected: `var prefix = $"Alberto.Postgres.{folderPath}.";` — matching the new embedded-resource names.

```bash
grep -rn 'DcbQuery\|DcbModuleBuilder' src/Alberto/DcbModuleBuilderExtensions.cs | head -3
```

Expected: the type names still read `DcbQuery` / `DcbModuleBuilder`. They must not have changed.

```bash
grep -rn 'Alberto DCB' src/*/*.csproj | head -3
```

Expected: descriptions still say "Alberto DCB event store". DCB as prose stays.

- [ ] **Step 7: Build everything CI builds**

```bash
dotnet build tests/Alberto.Tests/Alberto.Tests.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -30
```

Expected: `Build succeeded`.

```bash
dotnet build tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -20
```

Expected: `Build succeeded`.

```bash
dotnet build tools/Alberto.Cli/Alberto.Cli.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -20
```

Expected: `Build succeeded`.

- [ ] **Step 8: Run the full test suite (Docker must be running)**

```bash
dotnet test tests/Alberto.Tests/Alberto.Tests.csproj -c Release --no-build --logger "console;verbosity=normal"
```

Expected: all pass. The migration parity tests assert on the resource prefix `Alberto.Postgres.Migrations.` and are the direct proof that embedded resources moved with the assembly.

```bash
dotnet test tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj -c Release --no-build
```

Expected: all pass, including the GraphQL schema snapshot.

- [ ] **Step 9: Run the benchmark smoke run**

```bash
dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --job dry --anyCategories=smoke
```

Expected: completes without error.

- [ ] **Step 10: Run the pack smoke**

```bash
rm -rf /tmp/alberto-pack && for proj in Alberto Alberto.Commands Alberto.EntityFramework Alberto.InMemory Alberto.Messaging Alberto.Messaging.Postgres Alberto.Postgres Alberto.Telemetry Alberto.Testing Alberto.Testing.Xunit; do dotnet pack "src/$proj/$proj.csproj" -c Release --version-suffix ci -o /tmp/alberto-pack -v quiet --nologo; done && ls /tmp/alberto-pack/*.nupkg | xargs -n1 basename
```

Expected: exactly ten `.nupkg` files named `Alberto.0.1.0-ci.nupkg`, `Alberto.Commands.0.1.0-ci.nupkg`, `Alberto.EntityFramework.0.1.0-ci.nupkg`, `Alberto.InMemory.0.1.0-ci.nupkg`, `Alberto.Messaging.0.1.0-ci.nupkg`, `Alberto.Messaging.Postgres.0.1.0-ci.nupkg`, `Alberto.Postgres.0.1.0-ci.nupkg`, `Alberto.Telemetry.0.1.0-ci.nupkg`, `Alberto.Testing.0.1.0-ci.nupkg`, `Alberto.Testing.Xunit.0.1.0-ci.nupkg`.

- [ ] **Step 11: Commit**

```bash
git add -A && git commit -m "refactor!: rename Alberto.Dcb.* to Alberto.* across packages, assemblies and namespaces"
```

---

## Task 6: Record the consequences and reset the local store

Two things the rename changes that no test can catch: the migration journal, and the telemetry meter name. Both belong in the changelog, and the local Aspire volume has to go.

**Files:**
- Modify: `CHANGELOG.md`
- Verify: `Alberto.slnx`, `README.md`

**Interfaces:**
- Consumes: the completed rename from Task 5.

- [ ] **Step 1: Add the changelog entry**

At the top of `CHANGELOG.md`, under whatever "Unreleased" heading the file already uses (match its existing format — check with `head -20 CHANGELOG.md`), add:

```markdown
### Changed

- **Breaking — everything renamed from `Alberto.Dcb.*` to `Alberto.*`.** Package IDs, assemblies,
  namespaces and directories all moved together. `Alberto.Dcb` is now `Alberto`,
  `Alberto.Dcb.Postgres` is now `Alberto.Postgres`, and so on for all ten packages. Types whose
  names contain `Dcb` — `DcbQuery`, `DcbModuleBuilder` — are unchanged.
- **Breaking — `Alberto.Dcb.Postgres.Messaging` is now `Alberto.Messaging.Postgres`.** Package
  segments now read feature-first, matching `Alberto.Testing` / `Alberto.Testing.Xunit` and .NET
  convention generally.
- **Breaking — the command pipeline moved out of the core namespace.** `AlbertoStore`,
  `CommandPipeline<T>`, `BoundPipeline<T,S>`, `UnboundPipeline<T,S>`, `BoundDecision`,
  `UnboundDecision`, `DeciderExtensions` and `AlbertoStoreBuilderExtensions` now live in
  `Alberto.Commands`. `Result`, `Decision` and `Problem` are core types and stay in `Alberto`.
  Call sites need `using Alberto.Commands;`.
- **Breaking — existing PostgreSQL stores cannot be upgraded across this rename.** DbUp records
  each executed migration in `schemaversions` by its embedded-resource name, and those names
  carry the assembly name. Every script's recorded name changed, so DbUp sees all 34 as pending
  and a replay against a migrated database fails. Drop and recreate any store created before
  this version. No bridging script is provided; there were no published packages and no external
  consumers at the time of the rename.
- **Breaking — the OpenTelemetry meter and ActivitySource are now named `Alberto`**, not
  `Alberto.Dcb`. Update any collector filter, dashboard or alert that matches on the old name.

### Removed

- The operator CLI is no longer published as a NuGet tool. It had not packed since the
  `IsPackable` default changed, and it is not part of the 1.0 surface. Run it from the repo with
  `dotnet run --project tools/Alberto.Cli`.
```

- [ ] **Step 2: Verify the solution file and README came through**

```bash
grep -c "src/Alberto/Alberto.csproj\|src/Alberto.Messaging.Postgres/Alberto.Messaging.Postgres.csproj" Alberto.slnx
```

Expected: `2`

```bash
grep -n "dotnet add package" README.md | head
```

Expected: any install snippets read `Alberto.Postgres` etc., with no `.Dcb`.

- [ ] **Step 3: Confirm the solution still restores**

```bash
dotnet restore Alberto.slnx 2>&1 | tail -5
```

Expected: `Restore succeeded` — or Aspire-workload errors limited to `Alberto.AppHost` / `Alberto.Orders.Api`, which is the pre-existing state. Any "project file does not exist" error means a path in `Alberto.slnx` was missed; fix it and rerun.

- [ ] **Step 4: Drop the local Aspire database volume**

The dev store was migrated under the old resource names and can no longer be upgraded.

```bash
docker volume ls --format '{{.Name}}' | grep -i alberto
```

Then remove what that lists:

```bash
docker volume ls --format '{{.Name}}' | grep -i alberto | xargs -r docker volume rm
```

Expected: each named volume echoed back. If a volume is in use, stop the Aspire host first.

- [ ] **Step 5: Verify a clean migration against the new names**

```bash
dotnet test tests/Alberto.Tests/Alberto.Tests.csproj -c Release --filter "FullyQualifiedName~Migration"
```

Expected: all pass. These run against fresh Testcontainers databases, so a pass proves the renamed resources migrate a store from empty.

- [ ] **Step 6: Commit**

```bash
git add CHANGELOG.md && git commit -m "docs: record the rename's breaking consequences in the changelog"
```

**Merge PR 2 before starting PR 3.** Confirm CI is green and the `publish-packages` run on main pushes ten `Alberto.*` packages to GitHub Packages.

---

# PR 3 — Wire nuget.org

## Task 7: Add the tag trigger and the trusted-publishing push

GitHub Packages requires authentication even for public packages, so it cannot be the public distribution channel — it stays as the per-commit prerelease feed. nuget.org gets tagged releases only. Both live in `publish-packages.yml` because the trusted-publishing policy is bound to that filename.

The existing `paths` filter has to go. GitHub applies `paths` to tag pushes as well as branch pushes, so a release tag on a commit that touched no `src/**` file would be silently skipped — a silent non-publish is worse than a few extra prerelease builds. A `concurrency` group replaces it as the way to avoid stacked runs.

**Files:**
- Modify: `.github/workflows/publish-packages.yml` — `on:`, `concurrency:`, `permissions:`, the `Determine version` step, the `Pack` step, and two new steps at the end

**Interfaces:**
- Consumes: `artifacts/*.nupkg` verified by Task 2's allowlist.
- Requires: repository secret `NUGET_USER`, set to the nuget.org **profile name** — not an email address. Task 9 Step 1 covers adding it.
- Produces: `steps.version.outputs.suffix` (empty on a release) and `steps.version.outputs.release` (`true` / `false`).

- [ ] **Step 1: Replace the trigger block**

Replace lines 3-18 (`on:` through the end of `workflow_dispatch`) with:

```yaml
on:
  push:
    branches: [main]
    tags: ['v*']

  workflow_dispatch:
    inputs:
      version_suffix:
        description: 'Version suffix (e.g. beta.1, beta.2). Leave empty to use run number.'
        required: false
        type: string

# No `paths` filter: GitHub applies path filters to tag pushes too, so a release tag on a
# commit that happened not to touch src/** would be skipped without a word. A few extra
# prerelease builds are cheaper than a release that silently does not happen.
concurrency:
  group: publish-${{ github.ref }}
  cancel-in-progress: false
```

- [ ] **Step 2: Add the OIDC permission**

Replace lines 27-29 (the `permissions:` block) with:

```yaml
    permissions:
      packages: write
      contents: read
      id-token: write   # required by NuGet/login for trusted publishing
```

- [ ] **Step 3: Replace the version step**

Replace the whole `Determine version` step with:

```yaml
      # A tag publishes the release version verbatim; anything else publishes a prerelease.
      # The tag must agree with VersionPrefix, which is the single source of truth — a tag
      # that disagrees is a mistake worth failing on, not a version worth guessing at.
      - name: Determine version
        id: version
        run: |
          PREFIX=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props | head -1)
          if [ -z "$PREFIX" ]; then
            echo "::error::Could not read <VersionPrefix> from Directory.Build.props"
            exit 1
          fi

          if [ "${GITHUB_REF_TYPE}" = "tag" ]; then
            TAG_VERSION="${GITHUB_REF_NAME#v}"
            if [ "$TAG_VERSION" != "$PREFIX" ]; then
              echo "::error::Tag ${GITHUB_REF_NAME} does not match <VersionPrefix>${PREFIX}</VersionPrefix>. Update Directory.Build.props or retag."
              exit 1
            fi
            echo "release=true" >> "$GITHUB_OUTPUT"
            echo "suffix=" >> "$GITHUB_OUTPUT"
            echo "Release version: $PREFIX"
          else
            SUFFIX="${{ inputs.version_suffix }}"
            if [ -z "$SUFFIX" ]; then
              SUFFIX="beta.${{ github.run_number }}"
            fi
            echo "release=false" >> "$GITHUB_OUTPUT"
            echo "suffix=$SUFFIX" >> "$GITHUB_OUTPUT"
            echo "Prerelease version: $PREFIX-$SUFFIX"
          fi
```

- [ ] **Step 4: Make the pack step handle an empty suffix**

Replace the `Pack` step with:

```yaml
      - name: Pack
        run: |
          SUFFIX_ARG=""
          if [ -n "${{ steps.version.outputs.suffix }}" ]; then
            SUFFIX_ARG="--version-suffix ${{ steps.version.outputs.suffix }}"
          fi
          for proj in Alberto Alberto.Commands Alberto.EntityFramework Alberto.InMemory Alberto.Messaging Alberto.Messaging.Postgres Alberto.Postgres Alberto.Telemetry Alberto.Testing Alberto.Testing.Xunit; do
            dotnet pack "src/$proj/$proj.csproj" -c Release --no-build $SUFFIX_ARG -o artifacts
          done
```

- [ ] **Step 5: Add the nuget.org steps at the end of the job**

Append after the `Push symbols to GitHub Packages` step:

```yaml
      # Trusted publishing: the OIDC token this job is allowed to mint is exchanged for a
      # nuget.org API key valid for one hour. No long-lived key is stored anywhere. The
      # policy on nuget.org is bound to this repository AND this workflow filename — renaming
      # this file breaks publishing until the policy is edited to match.
      # NUGET_USER is the nuget.org profile name, not an email address.
      - name: Log in to nuget.org
        id: nuget-login
        if: steps.version.outputs.release == 'true'
        uses: NuGet/login@v1
        with:
          user: ${{ secrets.NUGET_USER }}

      # Runs only behind the "Verify packed set" allowlist. nuget.org pushes are permanent:
      # a package can be unlisted but never deleted, and the ID is never reclaimable.
      # .snupkg files sitting next to their .nupkg are uploaded automatically.
      - name: Push to nuget.org
        if: steps.version.outputs.release == 'true'
        run: |
          dotnet nuget push "artifacts/*.nupkg" \
            --source https://api.nuget.org/v3/index.json \
            --api-key "${{ steps.nuget-login.outputs.NUGET_API_KEY }}" \
            --skip-duplicate \
            --timeout 600
```

- [ ] **Step 6: Validate the workflow parses**

```bash
python3 -c "import yaml,sys; d=yaml.safe_load(open('.github/workflows/publish-packages.yml')); print(sorted(d[True].keys()) if True in d else sorted(d['on'].keys())); print([s.get('name') for s in d['jobs']['publish']['steps']])"
```

Expected: the trigger keys `['push', 'workflow_dispatch']`, and a step list ending `… 'Verify packed set', 'Add GitHub Packages source', 'Push libraries to GitHub Packages', 'Push symbols to GitHub Packages', 'Log in to nuget.org', 'Push to nuget.org']`. No `Push CLI to GitHub Packages`.

- [ ] **Step 7: Verify the tag/version guard by hand**

```bash
sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props | head -1
```

Expected: `0.1.0`. This is the value tag `v0.1.0` must match.

- [ ] **Step 8: Commit**

```bash
git add .github/workflows/publish-packages.yml && git commit -m "ci: publish tagged releases to nuget.org via trusted publishing"
```

**Merge PR 3.** The main-branch run must stay green and still push prereleases to GitHub Packages. No nuget.org push happens yet — there is no tag.

---

# PR 4 — Release

## Task 8: Promote the public API surface to Shipped

Every `PublicAPI.Shipped.txt` in the repo is a single `#nullable enable` line; all 1,912 declarations sit in `PublicAPI.Unshipped.txt`. That is correct for a library that has never shipped. Once `0.1.0` is on nuget.org it is no longer true, and leaving it means the analyzer will never flag a removed public member.

Promoting adds deliberate friction: removing a public API afterwards becomes an RS0017 error until you edit `PublicAPI.Shipped.txt` by hand. That is the point — it is the moment you learn you are breaking consumers. Do this task; it is the plan's chosen tradeoff, and nothing else depends on it either way.

**Files:**
- Modify: all `src/*/PublicAPI.Shipped.txt` and `src/*/PublicAPI.Unshipped.txt` for projects where the analyzer is active (`IsPackable=true`)

**Interfaces:**
- Consumes: the renamed API names from Task 5.

- [ ] **Step 1: Move Unshipped into Shipped for the ten packable projects**

```bash
for proj in Alberto Alberto.Commands Alberto.EntityFramework Alberto.InMemory Alberto.Messaging Alberto.Messaging.Postgres Alberto.Postgres Alberto.Telemetry Alberto.Testing Alberto.Testing.Xunit; do
  d="src/$proj"
  [ -f "$d/PublicAPI.Unshipped.txt" ] || { echo "SKIP $d"; continue; }
  { grep -hv '^#nullable enable$' "$d/PublicAPI.Shipped.txt" "$d/PublicAPI.Unshipped.txt" | grep -v '^[[:space:]]*$' | LC_ALL=C sort -u; } > /tmp/api-merged.txt
  { echo '#nullable enable'; cat /tmp/api-merged.txt; } > "$d/PublicAPI.Shipped.txt"
  echo '#nullable enable' > "$d/PublicAPI.Unshipped.txt"
  echo "$d: $(wc -l < "$d/PublicAPI.Shipped.txt") shipped lines"
done
```

Expected: ten lines, each reporting a shipped-line count greater than 1.

- [ ] **Step 2: Build — the analyzer is the test**

```bash
dotnet build tests/Alberto.Tests/Alberto.Tests.csproj -c Release 2>&1 | grep -E "RS00|error|Build succeeded" | head -30
```

Expected: `Build succeeded`. `RS0024` or `RS0025` means the sort order or a duplicate entry is wrong in the named file — re-sort that file with `LC_ALL=C sort` keeping `#nullable enable` on line 1. `RS0016` means a declaration is in neither file; add it to `PublicAPI.Unshipped.txt`.

- [ ] **Step 3: Confirm the parked projects were left alone**

```bash
cat src/Alberto.Admin/PublicAPI.Shipped.txt src/Alberto.Admin.Postgres/PublicAPI.Shipped.txt 2>/dev/null
```

Expected: only `#nullable enable` lines, or no such files. The analyzer is gated on `IsPackable` and both are parked, so their surface must not be frozen.

- [ ] **Step 4: Commit**

```bash
git add src/*/PublicAPI.Shipped.txt src/*/PublicAPI.Unshipped.txt && git commit -m "chore: promote the public API surface to Shipped ahead of 0.1.0"
```

---

## Task 9: Go public and tag 0.1.0

The remaining steps are outward-facing and permanent. Steps 1, 2 and 5 must be performed by the repository owner — do not run them on their behalf.

**Files:**
- No source changes. Repository settings, a secret, and a tag.

**Interfaces:**
- Consumes: the merged PR 3 workflow and the Task 8 commit on `main`.

- [ ] **Step 1: (Owner) Add the `NUGET_USER` secret**

In `codest-be/alberto` → Settings → Secrets and variables → Actions → New repository secret:
- Name: `NUGET_USER`
- Value: the nuget.org **profile name** shown at the top right of nuget.org — not an email address.

Verify it exists:

```bash
gh secret list --repo codest-be/alberto
```

Expected: `NUGET_USER` in the list.

- [ ] **Step 2: (Owner) Make the repository public**

A private repository produces a dead "Project website" link on every package page and SourceLink symbols nobody can resolve. Both are only fixed by making it public.

```bash
gh repo edit codest-be/alberto --visibility public --accept-visibility-change-consequences
```

Verify:

```bash
gh repo view codest-be/alberto --json visibility,url
```

Expected: `"visibility": "PUBLIC"`.

- [ ] **Step 3: Confirm the trusted-publishing policy is still within its window**

The policy was created with a 7-day "pending full activation" window; it becomes permanent on the first successful publish. If the window has lapsed, restart it on nuget.org — it can be restarted any number of times. Check the policy at https://www.nuget.org/account/trustedpublishing and confirm:
- Owner: `CODEST`
- Repository: `codest-be/alberto`
- Workflow file: `publish-packages.yml`

- [ ] **Step 4: Confirm `main` is green and at the right version**

```bash
gh run list --repo codest-be/alberto --workflow publish-packages.yml --limit 3
```

Expected: the most recent run on `main` is `success`.

```bash
git checkout main && git pull && sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props | head -1
```

Expected: `0.1.0`.

- [ ] **Step 5: (Owner) Tag and push**

This is the irreversible step. The push to nuget.org that follows cannot be undone — packages can be unlisted but never deleted, and the ten IDs are claimed permanently.

```bash
git tag -a v0.1.0 -m "Alberto 0.1.0 — first public release" && git push origin v0.1.0
```

- [ ] **Step 6: Watch the release run**

```bash
gh run watch --repo codest-be/alberto $(gh run list --repo codest-be/alberto --workflow publish-packages.yml --limit 1 --json databaseId --jq '.[0].databaseId')
```

Expected: every step succeeds, including `Verify packed set`, `Log in to nuget.org` and `Push to nuget.org`.

- [ ] **Step 7: Verify all ten packages are live**

Indexing takes a few minutes.

```bash
for p in Alberto Alberto.Commands Alberto.EntityFramework Alberto.InMemory Alberto.Messaging Alberto.Messaging.Postgres Alberto.Postgres Alberto.Telemetry Alberto.Testing Alberto.Testing.Xunit; do
  printf '%-28s %s\n' "$p" "$(curl -s "https://api.nuget.org/v3-flatcontainer/$(echo "$p" | tr 'A-Z' 'a-z')/index.json" | tr -d ' \n')"
done
```

Expected: each line reports `{"versions":["0.1.0"]}`.

- [ ] **Step 8: Confirm nothing extra was published**

```bash
for p in Alberto.Admin Alberto.Admin.Postgres Alberto.Cli Alberto.Commands.Analyzers Alberto.Dcb; do
  printf '%-28s %s\n' "$p" "$(curl -s -o /dev/null -w '%{http_code}' "https://api.nuget.org/v3-flatcontainer/$(echo "$p" | tr 'A-Z' 'a-z')/index.json")"
done
```

Expected: `404` for every one.

- [ ] **Step 9: Verify a real install from a clean machine's perspective**

```bash
SMOKE=$(mktemp -d) && cd "$SMOKE" && dotnet new classlib -f net10.0 -o consume >/dev/null && cd consume && dotnet add package Alberto.Postgres --version 0.1.0 --source https://api.nuget.org/v3/index.json && dotnet build
```

Expected: the restore pulls `Alberto.Postgres` **and** its transitive `Alberto`, and `Build succeeded`. An `NU1101` here means a dependency was not published — the one failure mode the allowlist cannot catch on its own, which is why `ci.yml`'s pack smoke exists.

- [ ] **Step 10: Create the GitHub release**

```bash
gh release create v0.1.0 --repo codest-be/alberto --title "Alberto 0.1.0" --notes "First public release. Ten packages on nuget.org: Alberto, Alberto.Commands, Alberto.EntityFramework, Alberto.InMemory, Alberto.Messaging, Alberto.Messaging.Postgres, Alberto.Postgres, Alberto.Telemetry, Alberto.Testing, Alberto.Testing.Xunit. See CHANGELOG.md for the breaking renames."
```

Expected: a release URL.

- [ ] **Step 11: Confirm the trusted-publishing policy went permanent**

The 7-day window ends at the first successful publish, which just happened. Reload https://www.nuget.org/account/trustedpublishing.

Expected: the `alberto-nuget` policy no longer shows a "use within 7 days" warning.

- [ ] **Step 12: (Owner) Request the `Alberto.` prefix reservation**

Only possible after a successful publish. Submit the request at https://www.nuget.org/account/Packages via the package-management contact form, asking to reserve the `Alberto.` prefix for owner `CODEST`.

"Alberto" is a common given name, so NuGet may scope the reservation narrowly or decline it. Nothing breaks either way — a declined reservation only means the packages do not carry the verified-owner check. Do not block the release on the outcome.

- [ ] **Step 13: Leave the orphaned GitHub packages alone**

The `Alberto.Dcb.*` prereleases already on GitHub Packages are orphans now. Nothing depends on them and deleting them buys nothing, so leave them. To confirm what is there:

```bash
gh api /orgs/codest-be/packages?package_type=nuget --jq '.[].name'
```

Expected: both the old `Alberto.Dcb.*` names and the new `Alberto.*` ones. If you would rather clean up, `gh api --method DELETE /orgs/codest-be/packages/nuget/<name>` removes one — but that is a preference, not a required step.
