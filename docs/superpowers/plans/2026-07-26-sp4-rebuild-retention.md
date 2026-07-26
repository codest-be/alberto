# SP4: Rebuild version retention — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop deleting projection state rows inside the transaction that changes the active version, so neither a reader holding a cached version nor a shadow loop that has not yet noticed an abort can be caught out.

**Architecture:** Promotion and abort stop calling `DeleteStateVersionAsync` and instead record the superseded or abandoned version in a new retirement table with a timestamp. The `RebuildCoordinator`'s existing sweep becomes retirement-aware: it collects a version only once its retirement is older than a configurable grace period. The grace cutoff is computed from an injected `TimeProvider` and passed into SQL as a parameter, never evaluated as server-side `now()`, which is what makes the whole thing testable with `FakeTimeProvider` instead of a sleep.

**Tech Stack:** .NET 10.0, PostgreSQL via Npgsql 10.0.2, DbUp-PostgreSQL 7.0.1, xUnit v3 3.2.2, `Microsoft.Extensions.TimeProvider.Testing` 10.5.0.

## Global Constraints

- Everything under `src/` multi-targets `net9.0;net10.0` and builds with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and `<GenerateDocumentationFile>true</GenerateDocumentationFile>`. Every new public member needs an XML doc comment or the build fails. Do not change the TFMs.
- `tests/Alberto.Dcb.Tests` targets `net10.0` only.
- NuGet versions are centrally managed in `Directory.Packages.props`. A `PackageReference` carries **no** `Version` attribute.
- **Every migration exists in two variants.** `src/Alberto.Dcb.Postgres/Migrations/` is multi-tenant; `src/Alberto.Dcb.Postgres/Migrations/SingleTenant/` is single-tenant. `MigrationUpgradeAndParityTests.SharedScriptNumbers_HaveIdenticalBaseNames_InBothVariants` asserts both directories agree on script numbers and base names, so a script added to one and not the other fails the suite.
- Migration scripts are embedded resources via `<EmbeddedResource Include="Migrations\*.sql" />`. A new `.sql` file needs no `.csproj` change.
- Migration scripts use the `$schema_prefix$` token before every table name. Never hardcode a schema.
- Migrations are immutable once shipped. Add `019_...`; never edit `018_...` or below.
- The suite must stay green. Run `dotnet test` before every commit and never commit red.
- PostgreSQL-backed tests use Testcontainers and require a running Docker daemon locally.
- Branch for this sub-project: `sp4-rebuild-retention`, off `main`.

### Scope boundary against SP2

SP2 is running concurrently in Wave 1 and also adds `TimeProvider` seams. It owns `CachingCheckpointStore`, `DeadLetterRetryLoop`, `ConsumeMiddleware`, `BatchConsumeMiddleware` and `InMemoryDeadLetterStore`. SP4 owns `RebuildCoordinator`, `RebuildableProjection`, `ProjectionVersions` and `PostgresProjectionRebuildStore`. **Do not touch SP2's files**, and do not unify the polling helpers — `ProjectionRebuildEndToEndTests.WaitUntilAsync` stays exactly as it is until SP1b.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/Alberto.Dcb.Postgres/Migrations/019_RebuildVersionRetention.sql` | Retirement table, multi-tenant | Create |
| `src/Alberto.Dcb.Postgres/Migrations/SingleTenant/019_RebuildVersionRetention.sql` | Retirement table, single-tenant | Create |
| `src/Alberto.Dcb/Subscriptions/IProjectionRebuildStore.cs` | Adds the collection method to the coordinator-only interface | Modify |
| `src/Alberto.Dcb.Postgres/PostgresProjectionRebuildStore.cs` | Retire instead of delete; implement collection | Modify |
| `src/Alberto.Dcb/Subscriptions/ProjectionVersions.cs` | Expose the refresh interval so grace can be validated against it | Modify |
| `src/Alberto.Dcb/Subscriptions/RebuildCoordinator.cs` | Grace option, validation, retirement-aware sweep | Modify |
| `src/Alberto.Dcb/DcbModuleBuilderExtensions.cs:518` | Pass the configured grace through | Modify |
| `tests/.../Subscriptions/RebuildRetentionTests.cs` | Retention behaviour at the store level | Create |
| `tests/.../Subscriptions/ProjectionRebuildEndToEndTests.cs` | Split the abort test; assert the new promise | Modify |

The retirement table is separate from `alberto_projection_rebuild_meta` because a processor can have several retired versions outstanding at once — a run of aborted rebuilds retires one version each — and that is a one-to-many relationship, not a column.

---

### Task 1: The retirement table

**Files:**
- Create: `src/Alberto.Dcb.Postgres/Migrations/019_RebuildVersionRetention.sql`
- Create: `src/Alberto.Dcb.Postgres/Migrations/SingleTenant/019_RebuildVersionRetention.sql`
- Test: `tests/Alberto.Dcb.Tests/Postgres/MigrationUpgradeAndParityTests.cs` (existing, no edit — it must keep passing)

**Interfaces:**
- Consumes: nothing.
- Produces: table `alberto_projection_version_retirements` with columns `processor_id TEXT NOT NULL`, `projection_type TEXT NOT NULL`, `version INTEGER NOT NULL`, `retired_at TIMESTAMPTZ NOT NULL`, primary key `(processor_id, version)`. Tasks 2 and 3 read and write it.

- [ ] **Step 1: Create the branch**

```bash
git switch -c sp4-rebuild-retention
```

- [ ] **Step 2: Write the multi-tenant migration**

Create `src/Alberto.Dcb.Postgres/Migrations/019_RebuildVersionRetention.sql`:

```sql
-- A promoted or aborted rebuild no longer deletes its dead version's state rows
-- inside the transaction that flips the version. Two parties can still be holding
-- that version at flip time: a reader whose ProjectionVersions cache has not
-- refreshed, and a shadow loop that has not yet polled the abort. Both used to
-- lose -- the reader by querying a version whose rows had just gone, the shadow
-- loop by writing rows after the delete had run.
--
-- The version is recorded here instead, and the coordinator's sweep collects it
-- once the retirement is older than the configured grace period.

CREATE TABLE IF NOT EXISTS $schema_prefix$alberto_projection_version_retirements (
    processor_id    TEXT        NOT NULL,
    projection_type TEXT        NOT NULL,
    version         INTEGER     NOT NULL,
    retired_at      TIMESTAMPTZ NOT NULL,
    CONSTRAINT alberto_projection_version_retirements_pkey
        PRIMARY KEY (processor_id, version)
);

-- The sweep asks "which retirements are older than this cutoff", across all
-- processors, on every coordinator tick.
CREATE INDEX IF NOT EXISTS alberto_projection_version_retirements_retired_at_idx
    ON $schema_prefix$alberto_projection_version_retirements (retired_at);

COMMENT ON TABLE $schema_prefix$alberto_projection_version_retirements IS
    'Rebuild versions that are no longer reachable but whose state rows are still present, pending the retention grace period.';

COMMENT ON COLUMN $schema_prefix$alberto_projection_version_retirements.retired_at IS
    'Supplied by the coordinator from its TimeProvider, never server-side now(): the grace period must be movable by a fake clock in tests.';
```

- [ ] **Step 3: Write the single-tenant migration**

Create `src/Alberto.Dcb.Postgres/Migrations/SingleTenant/019_RebuildVersionRetention.sql` with **byte-identical content** to Step 2. The retirement table has no `tenant_id` column in either variant — a rebuild is a schema-level operation spanning all tenants, exactly as the existing `DeleteStateVersionAsync` comment states.

- [ ] **Step 4: Verify parity and that both migrations apply**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~MigrationUpgradeAndParity"
```

Expected: PASS, 3 tests — `SharedScriptNumbers_HaveIdenticalBaseNames_InBothVariants`, `SingleTenant_UpgradesFromPre009Schema_ToCurrentMigrations_Successfully`, `MultiTenant_UpgradesFromPre009Schema_ToCurrentMigrations_Successfully`.

If the parity test fails, the two filenames differ — they must match exactly including case.

- [ ] **Step 5: Run the full suite and commit**

```bash
dotnet test
```

Expected: `Passed: 1040, Skipped: 15`.

```bash
git add src/Alberto.Dcb.Postgres/Migrations/019_RebuildVersionRetention.sql src/Alberto.Dcb.Postgres/Migrations/SingleTenant/019_RebuildVersionRetention.sql
git commit -m "feat: add rebuild version retention table

Migration 019 in both variants. Nothing writes to it yet."
```

---

### Task 2: Retire instead of delete

**Files:**
- Modify: `src/Alberto.Dcb/Subscriptions/IProjectionRebuildStore.cs:185-204`
- Modify: `src/Alberto.Dcb.Postgres/PostgresProjectionRebuildStore.cs:248-256, 293-300, 319-337`
- Test: `tests/Alberto.Dcb.Tests/Subscriptions/RebuildRetentionTests.cs` (create)

**Interfaces:**
- Consumes: the table from Task 1.
- Produces:
  - `IProjectionRebuildCoordinatorStore.CollectRetiredVersionsAsync(DateTimeOffset cutoff, CancellationToken ct = default)` returning `Task<IReadOnlyList<RetiredVersion>>`.
  - `public sealed record RetiredVersion(string ProcessorId, string ProjectionType, int Version, DateTimeOffset RetiredAt);` in `IProjectionRebuildStore.cs`.
  - `PostgresProjectionRebuildStore` gains a trailing `TimeProvider? timeProvider = null` constructor parameter.

  Task 3 calls `CollectRetiredVersionsAsync` and reads `ProcessorId` and `Version` off each returned record.

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Subscriptions/RebuildRetentionTests.cs`. Open `tests/Alberto.Dcb.Tests/Subscriptions/ProjectionRebuildStoreTests.cs` first and copy its fixture attribute, `using` block and helper for seeding state rows — this test needs the same private database and the same seeding, and reproducing it by hand will drift.

```csharp
[Fact]
public async Task Promotion_LeavesTheSupersededVersionsRowsInPlace()
{
    var time = new FakeTimeProvider();
    time.SetUtcNow(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    var store = CreateStore(time);
    var coordinator = (IProjectionRebuildCoordinatorStore)store;

    await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 10,
        TestContext.Current.CancellationToken);
    await SeedStateAsync(ProjectionType, version: 1, documentId: "doc-1");
    await coordinator.MarkReadyAsync(ProcessorId, TestContext.Current.CancellationToken);

    await coordinator.CompletePromotionAsync(ProcessorId, force: false,
        TestContext.Current.CancellationToken);

    // The reader-visible promise: a reader that resolved version 1 a moment before
    // the flip and queries it a moment after still finds its rows.
    Assert.Equal(1, await CountStateRowsAsync(ProjectionType, version: 1));
}

[Fact]
public async Task Promotion_RecordsTheSupersededVersionForCollection()
{
    var time = new FakeTimeProvider();
    var retiredAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    time.SetUtcNow(retiredAt);
    var store = CreateStore(time);
    var coordinator = (IProjectionRebuildCoordinatorStore)store;

    await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 10,
        TestContext.Current.CancellationToken);
    await coordinator.MarkReadyAsync(ProcessorId, TestContext.Current.CancellationToken);
    await coordinator.CompletePromotionAsync(ProcessorId, force: false,
        TestContext.Current.CancellationToken);

    // Nothing is collectable yet -- the cutoff is the retirement instant itself.
    Assert.Empty(await coordinator.CollectRetiredVersionsAsync(
        retiredAt, TestContext.Current.CancellationToken));

    var collectable = await coordinator.CollectRetiredVersionsAsync(
        retiredAt.AddSeconds(1), TestContext.Current.CancellationToken);

    var retired = Assert.Single(collectable);
    Assert.Equal(ProcessorId, retired.ProcessorId);
    Assert.Equal(1, retired.Version);
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~RebuildRetentionTests"
```

Expected: a build error, `CS1061: 'IProjectionRebuildCoordinatorStore' does not contain a definition for 'CollectRetiredVersionsAsync'`.

- [ ] **Step 3: Extend the coordinator-only interface**

In `src/Alberto.Dcb/Subscriptions/IProjectionRebuildStore.cs`, add the record immediately above `IProjectionRebuildCoordinatorStore`:

```csharp
/// <summary>
/// A rebuild version that is no longer reachable but whose state rows are still present,
/// waiting out the retention grace period.
/// </summary>
/// <param name="RetiredAt">
/// Stamped by the coordinator from its <see cref="TimeProvider"/>, not by the database. The
/// grace period has to be movable by a fake clock, which server-side <c>now()</c> is not.
/// </param>
public sealed record RetiredVersion(
    string ProcessorId,
    string ProjectionType,
    int Version,
    DateTimeOffset RetiredAt);
```

Then add to the `IProjectionRebuildCoordinatorStore` interface body, and update its two existing method summaries, which currently claim the transitions delete state rows:

```csharp
    /// <summary>
    /// Returns every retired version whose retirement is strictly older than
    /// <paramref name="cutoff"/>, and deletes its state rows in the same transaction.
    /// </summary>
    /// <remarks>
    /// Versions still inside the grace period are left alone: a reader holding a stale
    /// version number from its <see cref="ProjectionVersions"/> cache must still find rows,
    /// and a shadow loop that has not yet polled an abort must still be able to land its
    /// last writes before they are collected.
    /// </remarks>
    Task<IReadOnlyList<RetiredVersion>> CollectRetiredVersionsAsync(
        DateTimeOffset cutoff, CancellationToken ct = default);
```

Change `CompletePromotionAsync`'s summary from "Atomically flips the rebuilt version to active and deletes the superseded state rows." to:

```csharp
    /// <summary>
    /// Atomically flips the rebuilt version to active and retires the superseded version.
    /// Its state rows survive until <see cref="CollectRetiredVersionsAsync"/> collects them.
    /// The caller must already have stopped the shadow loop and verified checkpoint locality.
    /// </summary>
```

Change `CompleteAbortAsync`'s summary the same way, from "deletes the abandoned version's state rows" to "retires the abandoned version".

Also correct the `RebuildOutcome.DiscardedVersion` doc comment at lines 104-110, which states "State rows in `alberto_projection_states` are already gone — the transition deleted them in its own transaction." That is now false. Replace that sentence with:

```
/// State rows in <c>alberto_projection_states</c> are still present: the transition retired
/// the version rather than deleting it, and the coordinator's sweep collects it once the
/// retention grace period has elapsed.
```

- [ ] **Step 4: Add the TimeProvider seam to the Postgres store**

In `src/Alberto.Dcb.Postgres/PostgresProjectionRebuildStore.cs`, add a trailing `TimeProvider? timeProvider = null` parameter to the constructor and a backing field:

```csharp
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
```

Match the file's existing constructor style — if it uses a primary constructor, add the parameter there; if an explicit one, add it last and assign in the body. Either way the parameter is optional and trailing so no call site changes.

Add a `RetirementsTable` property beside the two existing ones at lines 39-40:

```csharp
    private string RetirementsTable => _schema.Table("alberto_projection_version_retirements");
```

- [ ] **Step 5: Replace the delete with a retire**

Replace `DeleteStateVersionAsync` (lines 323-337) with:

```csharp
    /// <summary>
    /// Records a version as unreachable without deleting its rows. Spans all tenants: a
    /// rebuild is a schema-level operation, not a per-tenant one.
    /// </summary>
    /// <remarks>
    /// <c>ON CONFLICT DO NOTHING</c> because a coordinator that crashes between the commit
    /// and its own bookkeeping may retire the same version twice, and the first timestamp is
    /// the honest one — restarting the grace period on a retry would extend it indefinitely
    /// under a crash loop.
    /// </remarks>
    private async Task RetireStateVersionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string processorId, string projectionType, int version, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {RetirementsTable} (processor_id, projection_type, version, retired_at)
            VALUES (@processor_id, @projection_type, @version, @retired_at)
            ON CONFLICT (processor_id, version) DO NOTHING
            """, conn, tx);

        cmd.Parameters.AddWithValue("processor_id", processorId);
        cmd.Parameters.AddWithValue("projection_type", projectionType);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("retired_at", _timeProvider.GetUtcNow());

        await cmd.ExecuteNonQueryAsync(ct);
    }
```

At line 251, in `CompletePromotionAsync`, replace:

```csharp
        // Drop the version being superseded in the same transaction as the flip. That is what
        // makes the swap invisible: readers see the old version complete, then the new version
        // complete, and never a half-deleted one.
        await DeleteStateVersionAsync(conn, tx, current.ProjectionType, current.ActiveVersion, ct);
```

with:

```csharp
        // Retire the superseded version rather than deleting it. Deleting here is what opened
        // the promotion read window: a reader resolves its version from a ProjectionVersions
        // cache and then queries it, and a promotion landing between those two steps left it
        // holding a version whose rows had just been removed.
        await RetireStateVersionAsync(
            conn, tx, processorId, current.ProjectionType, current.ActiveVersion, ct);
```

At line 295, in `CompleteAbortAsync`, replace:

```csharp
        // The active version is untouched, so discarding the partial rebuild is invisible
        // to readers.
        await DeleteStateVersionAsync(conn, tx, current.ProjectionType, abandonedVersion, ct);
```

with:

```csharp
        // The active version is untouched, so discarding the partial rebuild is invisible to
        // readers. The abandoned version is retired rather than deleted because the shadow
        // loop only learns of the abort on its next poll: deleting here raced its final writes,
        // which then landed after the delete and outlived the abort.
        await RetireStateVersionAsync(
            conn, tx, processorId, current.ProjectionType, abandonedVersion, ct);
```

- [ ] **Step 6: Implement collection**

Add to `PostgresProjectionRebuildStore`, beside the other explicit interface implementations:

```csharp
    /// <inheritdoc/>
    async Task<IReadOnlyList<RetiredVersion>> IProjectionRebuildCoordinatorStore.CollectRetiredVersionsAsync(
        DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Delete the retirement rows first and let RETURNING name what was claimed. Two
        // coordinators sweeping at once therefore split the work rather than both deleting
        // the same state rows, and neither reports a version the other already collected.
        var claimed = new List<RetiredVersion>();
        await using (var claimCmd = new NpgsqlCommand(
            $"""
            DELETE FROM {RetirementsTable}
            WHERE retired_at < @cutoff
            RETURNING processor_id, projection_type, version, retired_at
            """, conn, tx))
        {
            claimCmd.Parameters.AddWithValue("cutoff", cutoff);

            await using var reader = await claimCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claimed.Add(new RetiredVersion(
                    ProcessorId: reader.GetString(0),
                    ProjectionType: reader.GetString(1),
                    Version: reader.GetInt32(2),
                    RetiredAt: reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        foreach (var retired in claimed)
        {
            await using var deleteCmd = new NpgsqlCommand(
                $"""
                DELETE FROM {StatesTable}
                WHERE projection_type = @projection_type AND rebuild_version = @version
                """, conn, tx);

            deleteCmd.Parameters.AddWithValue("projection_type", retired.ProjectionType);
            deleteCmd.Parameters.AddWithValue("version", retired.Version);
            await deleteCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return claimed;
    }
```

- [ ] **Step 7: Run the tests and verify they pass**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~RebuildRetentionTests"
```

Expected: PASS, 2 tests.

- [ ] **Step 8: Run the full suite**

```bash
dotnet test
```

Expected: `Passed: 1042, Skipped: 15`.

**If `ProjectionRebuildEndToEndTests` or `StateStoreRebuildVersionTests` now fail**, that is the expected consequence of this task: state rows that used to vanish at promotion now survive, and any test asserting immediate emptiness is asserting the old promise. Do **not** weaken those assertions here — Task 4 rewrites them deliberately. If the failures are confined to those two classes, commit anyway with the note below; if anything else fails, stop and diagnose.

- [ ] **Step 9: Commit**

```bash
git add src/Alberto.Dcb/Subscriptions/IProjectionRebuildStore.cs src/Alberto.Dcb.Postgres/PostgresProjectionRebuildStore.cs tests/Alberto.Dcb.Tests/Subscriptions/RebuildRetentionTests.cs
git commit -m "feat: retire rebuild versions instead of deleting them

Promotion and abort now record the dead version in
alberto_projection_version_retirements with a TimeProvider-supplied
timestamp, rather than deleting its state rows inside the transaction
that flips the version. CollectRetiredVersionsAsync does the deleting,
claiming rows by DELETE ... RETURNING so concurrent coordinators split
the work instead of racing.

Nothing calls the collector yet -- that is the next commit -- so dead
versions currently accumulate."
```

---

### Task 3: The retirement-aware sweep

The coordinator's existing `SweepAsync` clears every version that is neither active nor rebuilding, immediately. Left as-is it would collect the just-superseded version on the very next tick and reintroduce exactly the race Task 2 removed.

**Files:**
- Modify: `src/Alberto.Dcb/Subscriptions/ProjectionVersions.cs:52-56`
- Modify: `src/Alberto.Dcb/Subscriptions/RebuildCoordinator.cs:28, 47-56, 374-395`
- Modify: `src/Alberto.Dcb/DcbModuleBuilderExtensions.cs:518`
- Test: `tests/Alberto.Dcb.Tests/Subscriptions/RebuildRetentionTests.cs`

**Interfaces:**
- Consumes: `CollectRetiredVersionsAsync` and `RetiredVersion` from Task 2.
- Produces:
  - `ProjectionVersions.RefreshInterval` — a `public TimeSpan` get-only property.
  - `RebuildCoordinatorOptions(TimeSpan PollingInterval, bool AutoPromote, TimeSpan? RetentionGrace = null)`.
  - `RebuildCoordinator` gains a trailing `TimeProvider? timeProvider = null` parameter.

- [ ] **Step 1: Write the failing test**

Add to `tests/Alberto.Dcb.Tests/Subscriptions/RebuildRetentionTests.cs`:

```csharp
[Fact]
public async Task Sweep_LeavesARetiredVersionAloneUntilTheGraceElapses()
{
    var time = new FakeTimeProvider();
    var retiredAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    time.SetUtcNow(retiredAt);
    var store = CreateStore(time);
    var coordinator = (IProjectionRebuildCoordinatorStore)store;
    var grace = TimeSpan.FromSeconds(30);

    await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 10,
        TestContext.Current.CancellationToken);
    await SeedStateAsync(ProjectionType, version: 1, documentId: "doc-1");
    await coordinator.MarkReadyAsync(ProcessorId, TestContext.Current.CancellationToken);
    await coordinator.CompletePromotionAsync(ProcessorId, force: false,
        TestContext.Current.CancellationToken);

    // One second short of the grace period: still there.
    time.Advance(grace - TimeSpan.FromSeconds(1));
    await coordinator.CollectRetiredVersionsAsync(
        time.GetUtcNow() - grace, TestContext.Current.CancellationToken);
    Assert.Equal(1, await CountStateRowsAsync(ProjectionType, version: 1));

    // Two seconds past it: collected.
    time.Advance(TimeSpan.FromSeconds(2));
    await coordinator.CollectRetiredVersionsAsync(
        time.GetUtcNow() - grace, TestContext.Current.CancellationToken);
    Assert.Equal(0, await CountStateRowsAsync(ProjectionType, version: 1));
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~Sweep_LeavesARetiredVersionAlone"
```

Expected: FAIL on the first assertion — `Assert.Equal() Failure: Values differ. Expected: 1, Actual: 0` — because nothing yet stops the collector from taking it. If it passes at this point, the cutoff arithmetic in the test is wrong; recheck before proceeding.

- [ ] **Step 3: Expose the refresh interval**

In `src/Alberto.Dcb/Subscriptions/ProjectionVersions.cs`, the constructor currently discards its interval into `RefreshLoopAsync`. Capture it:

```csharp
    /// <summary>
    /// How often the rebuild state is re-read. The retention grace period must exceed this:
    /// a version collected sooner could still be held by a replica that has not refreshed.
    /// </summary>
    public TimeSpan RefreshInterval { get; }

    public ProjectionVersions(IProjectionRebuildStore store, TimeSpan? refreshInterval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        RefreshInterval = refreshInterval ?? TimeSpan.FromSeconds(5);
        _refreshLoop = RefreshLoopAsync(RefreshInterval);
    }
```

- [ ] **Step 4: Add the grace option and validate it**

In `src/Alberto.Dcb/Subscriptions/RebuildCoordinator.cs`, change line 28 to:

```csharp
internal sealed record RebuildCoordinatorOptions(
    TimeSpan PollingInterval,
    bool AutoPromote,
    TimeSpan? RetentionGrace = null);
```

Add a trailing `TimeProvider? timeProvider = null` to the primary constructor at lines 47-56, then add to the class body:

```csharp
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// How long a retired version's rows survive before the sweep collects them. Defaults to
    /// four times the version refresh interval with a floor of 30 seconds. The multiplier buys
    /// headroom for a failed refresh, which RefreshLoopAsync deliberately rides out by leaving
    /// the previous versions in place.
    /// </summary>
    private TimeSpan RetentionGrace => options.RetentionGrace ?? DefaultGrace();

    private TimeSpan DefaultGrace()
    {
        var fromRefresh = versions.RefreshInterval * 4;
        return fromRefresh > MinimumGrace ? fromRefresh : MinimumGrace;
    }

    private static readonly TimeSpan MinimumGrace = TimeSpan.FromSeconds(30);
```

Then validate at the top of `ExecuteAsync` (line 60), before the startup sweep:

```csharp
        // A grace shorter than either interval it depends on reintroduces the exact race this
        // design removes, and would do it silently -- so it is a startup failure, not a warning.
        if (RetentionGrace <= versions.RefreshInterval)
            throw new InvalidOperationException(
                $"Rebuild retention grace ({RetentionGrace}) must exceed the projection version " +
                $"refresh interval ({versions.RefreshInterval}): a version collected sooner could " +
                $"still be held by a replica that has not refreshed.");

        if (RetentionGrace <= options.PollingInterval)
            throw new InvalidOperationException(
                $"Rebuild retention grace ({RetentionGrace}) must exceed the coordinator polling " +
                $"interval ({options.PollingInterval}): a shadow loop that has not yet polled an " +
                $"abort could still land writes after its version was collected.");
```

- [ ] **Step 5: Make the sweep retirement-aware**

Replace `SweepAsync` (lines 374-389) with:

```csharp
    private async Task SweepAsync(
        RebuildableProjection projection, ProjectionRebuildState state, CancellationToken ct)
    {
        // Version numbers are monotonic, so every dead version sits below the highest one this
        // processor knows about. Bounding on the active version alone would leave the versions
        // that a run of aborted rebuilds burned through unswept, since abort does not advance it.
        var highest = state.LastAllocatedVersion;
        var stillRetained = await RetainedVersionsAsync(projection.ProcessorId, ct);

        for (var version = ProjectionVersions.Initial; version <= highest; version++)
        {
            if (version == state.ActiveVersion || version == state.RebuildingVersion)
                continue;

            // Inside its grace period: a reader or a shadow loop may still be holding it.
            if (stillRetained.Contains(version))
                continue;

            await ClearAsync(projection.ProcessorId, version, ct);
        }
    }
```

Add the two helpers below it:

```csharp
    /// <summary>
    /// Collects everything past its grace period -- which also deletes the state rows in this
    /// database -- and returns the versions for this processor that are still retained, so the
    /// caller leaves external backends' copies of them alone too.
    /// </summary>
    private async Task<HashSet<int>> RetainedVersionsAsync(string processorId, CancellationToken ct)
    {
        var cutoff = _timeProvider.GetUtcNow() - RetentionGrace;
        var collected = await rebuildStore.CollectRetiredVersionsAsync(cutoff, ct);

        foreach (var retired in collected.Where(r => r.ProcessorId == processorId))
            await ClearAsync(retired.ProcessorId, retired.Version, ct);

        return await RetainedAfterCollectionAsync(processorId, ct);
    }

    /// <summary>
    /// Versions still inside the grace period after the collection pass. Read back rather than
    /// inferred, because a concurrent coordinator may have collected some of them.
    /// </summary>
    private async Task<HashSet<int>> RetainedAfterCollectionAsync(
        string processorId, CancellationToken ct)
    {
        var remaining = await rebuildStore.CollectRetiredVersionsAsync(
            DateTimeOffset.MinValue, ct);
        return remaining.Where(r => r.ProcessorId == processorId).Select(r => r.Version).ToHashSet();
    }
```

Note the second helper passes `DateTimeOffset.MinValue`, so it collects nothing and acts as a read. If that reads as too clever when you get there, add a separate `ListRetiredVersionsAsync` to the interface instead and use it — but then update the Interfaces block of this task and Task 2 to match.

- [ ] **Step 6: Pass the configured grace through**

At `src/Alberto.Dcb/DcbModuleBuilderExtensions.cs:518`, the options are built as:

```csharp
                var coordinatorOptions = new RebuildCoordinatorOptions(opts.PollingInterval, opts.AutoPromote);
```

Change to:

```csharp
                var coordinatorOptions = new RebuildCoordinatorOptions(
                    opts.PollingInterval, opts.AutoPromote, opts.RetentionGrace);
```

Then add `RetentionGrace` to whichever options type `opts` is — find it by following the enclosing lambda — as:

```csharp
    /// <summary>
    /// How long a superseded or abandoned rebuild version's state rows survive before the
    /// coordinator collects them. Null uses four times the version refresh interval, floored
    /// at 30 seconds. Must exceed both the refresh interval and the polling interval; the
    /// coordinator refuses to start otherwise.
    /// </summary>
    public TimeSpan? RetentionGrace { get; set; }
```

- [ ] **Step 7: Run the test and verify it passes**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~RebuildRetentionTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 8: Add the validation test**

Add to `tests/Alberto.Dcb.Tests/Subscriptions/RebuildRetentionTests.cs`:

```csharp
[Fact]
public async Task Coordinator_RefusesToStart_WhenGraceIsShorterThanTheRefreshInterval()
{
    var versions = new ProjectionVersions(CreateStore(), refreshInterval: TimeSpan.FromSeconds(5));
    await using var _ = versions;

    var coordinator = CreateCoordinator(
        versions,
        new RebuildCoordinatorOptions(
            PollingInterval: TimeSpan.FromSeconds(1),
            AutoPromote: false,
            RetentionGrace: TimeSpan.FromSeconds(2)));

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
        () => coordinator.StartAsync(TestContext.Current.CancellationToken));

    Assert.Contains("must exceed the projection version refresh interval", ex.Message);
}
```

`CreateCoordinator` is a helper you write in this file, assembling a `RebuildCoordinator` from the fixture's store plus empty projection and clearer lists. Model it on how `ProjectionRebuildEndToEndTests` builds its host, but without the control loop — this test never reaches the sweep.

- [ ] **Step 9: Run the full suite and commit**

```bash
dotnet test
```

Expected: `Passed: 1044, Skipped: 15`, with `ProjectionRebuildEndToEndTests` possibly still failing per Task 2 Step 8. Task 4 resolves those.

```bash
git add src/Alberto.Dcb/Subscriptions/ProjectionVersions.cs src/Alberto.Dcb/Subscriptions/RebuildCoordinator.cs src/Alberto.Dcb/DcbModuleBuilderExtensions.cs tests/Alberto.Dcb.Tests/Subscriptions/RebuildRetentionTests.cs
git commit -m "feat: retirement-aware rebuild sweep

The sweep now collects a dead version only once its retirement is older
than the retention grace, defaulting to 4x the version refresh interval
with a 30s floor. A grace shorter than either the refresh interval or
the polling interval is a startup failure rather than a warning: it
reintroduces the race this design removes, and does so silently."
```

---

### Task 4: Rewrite the two tests that asserted the old promise

**Files:**
- Modify: `tests/Alberto.Dcb.Tests/Subscriptions/ProjectionRebuildEndToEndTests.cs`
- Modify: `CLAUDE.md` — the first two Known Gaps entries

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: no new surface.

- [ ] **Step 1: Fix the promotion test**

`Rebuild_ReplacesCorruptedState_WithoutEverServingAPartialProjection` should now pass unchanged — the defect it was catching is gone. Run it:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~Rebuild_ReplacesCorruptedState"
```

Expected: PASS. If it fails, the retention plumbing is wrong — diagnose before continuing, do not adjust the test.

- [ ] **Step 2: Split the abort test**

`AbortedRebuild_LeavesTheLiveVersionUntouched` currently asserts both that the live version survives and that the abandoned version's rows are gone immediately. The second half is now asserting something the design explicitly does not promise.

Keep the existing test, narrowed to its name:

```csharp
[Fact]
public async Task AbortedRebuild_LeavesTheLiveVersionUntouched()
{
    // ... existing arrange and abort, unchanged ...

    // The live version is the whole promise here. The abandoned version's rows are
    // deliberately still present -- see AbortedVersion_IsCollectedAfterTheRetentionGrace.
    Assert.Equal(expectedLiveRows, await CountStateRowsAsync(ProjectionType, liveVersion));
}
```

Delete the assertion about the abandoned version being empty, and add a second test in its place:

```csharp
[Fact]
public async Task AbortedVersion_IsCollectedAfterTheRetentionGrace()
{
    // ... same arrange and abort as above ...

    // Immediately after the abort the rows are still there by design: the shadow loop
    // only learns of the abort on its next poll, and deleting now would race its final
    // writes -- which is what used to leave rows behind after the delete had run.
    Assert.NotEqual(0, await CountStateRowsAsync(ProjectionType, abandonedVersion));

    time.Advance(grace + TimeSpan.FromSeconds(1));
    await WaitUntilAsync(
        async () => await CountStateRowsAsync(ProjectionType, abandonedVersion) == 0,
        "abandoned version collected",
        TestContext.Current.CancellationToken);
}
```

This test needs the host built with a `FakeTimeProvider`; thread one through however the file's existing host builder takes overrides. Use the existing `WaitUntilAsync` helper at line 458 rather than adding a new one — SP1b unifies it later.

- [ ] **Step 3: Run the rebuild tests**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~ProjectionRebuild"
```

Expected: PASS, with one more test than before the split.

- [ ] **Step 4: Verify under the load that surfaced the defects**

A single green run proves nothing here — both defects were roughly one run in twenty, and never reproduced on an unloaded machine. Run:

```bash
for i in $(seq 1 20); do
  echo "=== run $i"
  for p in 1 2 3 4 5; do
    dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj \
      --filter "FullyQualifiedName~ProjectionRebuildEndToEndTests" \
      --logger "console;verbosity=quiet" &
  done
  wait
done
```

Expected: 100 passing runs, zero failures. Any failure means the grace period is not covering a window the design assumed — capture the output and diagnose rather than re-running until it passes.

- [ ] **Step 5: Update the Known Gaps**

In `CLAUDE.md`, delete the first two entries under "Known Gaps" — "Promotion opens a one-query window where a reader sees nothing" and "An aborted version's rows can outlive the abort by a tick". Both are now closed.

In their place, under Key Patterns, extend the zero-downtime rebuild bullet:

```markdown
- **Zero-downtime projection rebuilds**: opt in with `.WithControlLoop(loop => loop.WithRebuilds())`. `RebuildCoordinator` replays the log into a shadow copy of a projection's state under its own checkpoint, then swaps versions in one transaction. Promotion and abort **retire** the dead version rather than deleting it; the coordinator's sweep collects it once the retention grace has elapsed, so a reader holding a stale version still finds rows and a shadow loop that has not yet polled an abort can still land its final writes. Grace defaults to 4× the version refresh interval, floored at 30s, and the coordinator refuses to start if it is shorter than either the refresh or polling interval. Driven by `alberto ops rebuild start|status|promote|abort`
```

- [ ] **Step 6: Run the full suite and commit**

```bash
dotnet test
```

Expected: `Passed: 1045, Skipped: 15`, exit code 0.

```bash
git add tests/Alberto.Dcb.Tests/Subscriptions/ProjectionRebuildEndToEndTests.cs CLAUDE.md
git commit -m "test: assert the retention promise, not the old one

AbortedRebuild_LeavesTheLiveVersionUntouched now asserts only what its
name claims. The abandoned version's collection moves to its own test
that advances a fake clock past the grace period, because asserting
emptiness immediately after an abort was asserting something the design
never promised -- which is why it failed one run in twenty.

Closes the first two Known Gaps in CLAUDE.md."
```

- [ ] **Step 7: Push and open the PR**

```bash
git push -u origin sp4-rebuild-retention
```

Open a PR titled `SP4: retire rebuild versions instead of deleting them`. Paste the Step 4 loop's result into the description — the reviewer needs to see 100 clean runs, not one.

---

## Self-Review

**Spec coverage.** The spec's SP4 section asks for: promote and abort to stamp the version retired rather than delete (Task 2); a sweep bounded by a grace period exceeding both the refresh and poll intervals (Task 3); migration 019 in both variants (Task 1); the cutoff computed caller-side from a `TimeProvider` rather than server-side `now()` (Task 2 Step 5, Task 3 Step 5); the grace default of 4× refresh floored at 30s with startup validation (Task 3 Steps 4 and 8); the promotion test becoming genuinely true and the abort test splitting in two (Task 4); and verification under five concurrent processes (Task 4 Step 4). All covered.

The spec also says "The existing abort sweep becomes the general collector" — Task 3 Step 5 rewrites `SweepAsync` in place rather than adding a parallel mechanism, which is that.

**Placeholder scan.** Task 3 Step 5 flags its own `DateTimeOffset.MinValue` read-back as possibly too clever and names the concrete alternative plus what else would need updating. Task 3 Step 6 says to find the options type "by following the enclosing lambda" rather than naming it, because `opts` at `DcbModuleBuilderExtensions.cs:518` was not read while writing this plan — the property to add is given in full. Task 4 Steps 1 and 2 describe edits to tests whose current bodies were not read in full; the assertions to delete and add are given exactly, the surrounding arrange is left as "unchanged".

**Type consistency.** `RetiredVersion` has the same four members in Task 2's Interfaces block, its definition in Step 3, its construction in Step 6, and its use in Task 3 Step 5. `CollectRetiredVersionsAsync(DateTimeOffset cutoff, CancellationToken ct)` has one signature throughout. `RetentionGrace` is the name of the option, the property and the CLAUDE.md text. `MinimumGrace` is used only in `DefaultGrace()`. `ProjectionVersions.RefreshInterval` is defined in Task 3 Step 3 and consumed in Steps 4 and 5.
