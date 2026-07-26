using Alberto.Dcb.EntityFramework;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tests.EntityFramework;
using Alberto.Dcb.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

// ---------------------------------------------------------------------------
// Shared TState for InMemory and Postgres adapters
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal state type used by InMemory and Postgres spec subclasses.
/// Postgres serialises it as JSONB; InMemory stores it by value.
/// </summary>
public sealed record SimpleState
{
    public int Value { get; init; }
}

// ---------------------------------------------------------------------------
// Abstract specification
// ---------------------------------------------------------------------------

/// <summary>
/// Conformance specification for <see cref="IStateStore{TState}"/>.
///
/// Every fact here is a property the caller is allowed to rely on. Each
/// adapter subclass inherits all facts; divergences are marked with an
/// <c>Assert.Skip</c> referencing the capability hook that is false for that
/// adapter, together with an explicit reason string.
/// </summary>
public abstract class StateStoreSpecification<TState>
{
    /// <summary>
    /// Short identifier that makes document IDs unique across concurrent test runs.
    /// </summary>
    protected string TestId { get; } = Guid.NewGuid().ToString("N")[..8];

    // ── Capability hooks ─────────────────────────────────────────────────────

    /// <summary>
    /// True when two <c>CreateStore</c> calls with different <paramref name="projectionType"/>
    /// strings are isolated from each other.
    ///
    /// InMemory and Postgres: <c>true</c> — projectionType is a first-class discriminator.
    /// EF: <c>false</c> — isolation is structural (entity type → table); the projectionType
    /// parameter is accepted by the spec seam but is not forwarded to
    /// <see cref="EfStateStore{TEntity,TDbContext}"/>, so two stores of the same TEntity
    /// always share one table.
    /// </summary>
    protected virtual bool SupportsProjectionTypeIsolation => true;

    /// <summary>
    /// True when two store instances created with the same <paramref name="projectionType"/>
    /// share a backing store (database, etc.) so reads from one see writes from the other.
    ///
    /// InMemory: <c>false</c> — each instance owns a private dictionary.
    /// Postgres and EF: <c>true</c> — both point at the same database.
    /// </summary>
    protected virtual bool SupportsSharedBackingStore => true;

    /// <summary>
    /// True when two stores created with different <c>tenantId</c> values for the same
    /// <paramref name="projectionType"/> are isolated from each other.
    ///
    /// Only the multi-tenant <see cref="PostgresStateStore{TState}"/> (constructed with
    /// a non-null <c>tenantId</c> on a multi-tenant schema) implements this.
    /// InMemory and EF: <c>false</c>.
    /// </summary>
    protected virtual bool SupportsTenantIsolation => false;

    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a store scoped to <paramref name="projectionType"/>, optionally
    /// with a custom rebuild-version selector.
    /// </summary>
    protected abstract Task<IStateStore<TState>> CreateStore(
        string projectionType,
        Func<int>? rebuildVersion = null);

    /// <summary>
    /// Creates a store scoped to both <paramref name="projectionType"/> and
    /// <paramref name="tenantId"/>. Only called when
    /// <see cref="SupportsTenantIsolation"/> is <c>true</c>; subclasses that
    /// set it to <c>true</c> must override this.
    /// </summary>
    protected virtual Task<IStateStore<TState>> CreateStoreForTenant(
        string projectionType,
        string tenantId,
        Func<int>? rebuildVersion = null)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support tenant-scoped stores " +
            "(SupportsTenantIsolation is false).");

    /// <summary>Returns a distinguishable state value carrying <paramref name="value"/>.</summary>
    protected abstract TState MakeState(int value);

    /// <summary>Extracts the distinguishing value from a state instance.</summary>
    protected abstract int ReadValue(TState state);

    /// <summary>Generates a fresh projection-type string per invocation.</summary>
    protected string NewProjectionType() =>
        $"spec-{TestId}-{Guid.NewGuid():N}";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Dictionary<string, TState> MakeDict(string docId, int value) =>
        new() { [docId] = MakeState(value) };

    // ── Spec facts — core read/write contract ─────────────────────────────────

    [Fact]
    public async Task LoadMany_EmptyIds_ReturnsEmptyDictionary()
    {
        var store = await CreateStore(NewProjectionType());

        var result = await store.LoadManyAsync([], Ct);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadMany_NonExistentIds_ReturnsEmptyDictionary()
    {
        var store = await CreateStore(NewProjectionType());

        var result = await store.LoadManyAsync(
            [$"missing-{TestId}-a", $"missing-{TestId}-b"], Ct);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyChanges_Upsert_ThenLoadMany_ReturnsStoredState()
    {
        var store = await CreateStore(NewProjectionType());
        var docId = $"doc-{TestId}-upsert";

        await store.ApplyChangesAsync(MakeDict(docId, 42), [], Ct);

        var result = await store.LoadManyAsync([docId], Ct);
        result.Should().ContainKey(docId);
        ReadValue(result[docId]).Should().Be(42);
    }

    [Fact]
    public async Task ApplyChanges_SecondUpsert_OverwritesPreviousValue()
    {
        var store = await CreateStore(NewProjectionType());
        var docId = $"doc-{TestId}-overwrite";

        await store.ApplyChangesAsync(MakeDict(docId, 10), [], Ct);
        await store.ApplyChangesAsync(MakeDict(docId, 20), [], Ct);

        ReadValue((await store.LoadManyAsync([docId], Ct))[docId]).Should().Be(20);
    }

    [Fact]
    public async Task ApplyChanges_Delete_RemovesDocument()
    {
        var store = await CreateStore(NewProjectionType());
        var docId = $"doc-{TestId}-delete";

        await store.ApplyChangesAsync(MakeDict(docId, 1), [], Ct);
        await store.ApplyChangesAsync(new Dictionary<string, TState>(), [docId], Ct);

        (await store.LoadManyAsync([docId], Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyChanges_DeleteNonExistent_DoesNotThrow()
    {
        var store = await CreateStore(NewProjectionType());

        var act = async () =>
            await store.ApplyChangesAsync(
                new Dictionary<string, TState>(), [$"doc-{TestId}-absent"], Ct);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplyChanges_EmptyBatch_DoesNotThrow()
    {
        var store = await CreateStore(NewProjectionType());

        var act = async () =>
            await store.ApplyChangesAsync(new Dictionary<string, TState>(), [], Ct);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplyChanges_UpsertAndDelete_BothAppliedInOneBatch()
    {
        var store = await CreateStore(NewProjectionType());
        var keepId = $"doc-{TestId}-keep";
        var dropId = $"doc-{TestId}-drop";

        await store.ApplyChangesAsync(
            new Dictionary<string, TState>
            {
                [keepId] = MakeState(1),
                [dropId] = MakeState(2),
            }, [], Ct);

        await store.ApplyChangesAsync(MakeDict(keepId, 99), [dropId], Ct);

        var result = await store.LoadManyAsync([keepId, dropId], Ct);
        result.Should().ContainKey(keepId);
        result.Should().NotContainKey(dropId);
        ReadValue(result[keepId]).Should().Be(99);
    }

    [Fact]
    public async Task LoadMany_PartialIds_ReturnsOnlyExistingDocuments()
    {
        var store = await CreateStore(NewProjectionType());
        var existId = $"doc-{TestId}-exists";
        var missId = $"doc-{TestId}-miss";

        await store.ApplyChangesAsync(MakeDict(existId, 5), [], Ct);

        var result = await store.LoadManyAsync([existId, missId], Ct);
        result.Should().HaveCount(1).And.ContainKey(existId);
    }

    // ── Spec fact — projectionType isolation ──────────────────────────────────

    /// <summary>
    /// Two stores created with different projectionType strings must not see each
    /// other's data for the same document ID.
    ///
    /// Skipped for EF: <see cref="SupportsProjectionTypeIsolation"/> is false because
    /// <c>EfStateStore&lt;CounterEntity&gt;</c> always writes to the same table.
    /// </summary>
    [Fact]
    public async Task ProjectionType_Isolates_SameDocId()
    {
        if (!SupportsProjectionTypeIsolation)
            Assert.Skip(
                "EfStateStore<TEntity> isolates projections structurally via entity type " +
                "(table), not via a projectionType string parameter. Two stores of the " +
                "same TEntity share one table and are not isolated by projectionType. " +
                "Use a different TEntity per projection to achieve isolation.");

        var storeA = await CreateStore(NewProjectionType());
        var storeB = await CreateStore(NewProjectionType());
        var docId = $"doc-{TestId}-shared";

        await storeA.ApplyChangesAsync(MakeDict(docId, 11), [], Ct);
        await storeB.ApplyChangesAsync(MakeDict(docId, 22), [], Ct);

        var rA = await storeA.LoadManyAsync([docId], Ct);
        var rB = await storeB.LoadManyAsync([docId], Ct);

        ReadValue(rA[docId]).Should().Be(11, "storeA must not see storeB's data");
        ReadValue(rB[docId]).Should().Be(22, "storeB must not see storeA's data");
    }

    /// <summary>
    /// A delete in one projectionType must not remove data in another projectionType even
    /// when both stores use the same document ID.
    ///
    /// Skipped for EF: <see cref="SupportsProjectionTypeIsolation"/> is false.
    /// </summary>
    [Fact]
    public async Task ProjectionType_DeleteInOne_DoesNotAffectOther()
    {
        if (!SupportsProjectionTypeIsolation)
            Assert.Skip(
                "EfStateStore<TEntity> does not isolate by projectionType; " +
                "see ProjectionType_Isolates_SameDocId for the full reason.");

        var storeA = await CreateStore(NewProjectionType());
        var storeB = await CreateStore(NewProjectionType());
        var docId = $"doc-{TestId}-del-iso";

        await storeA.ApplyChangesAsync(MakeDict(docId, 11), [], Ct);
        await storeB.ApplyChangesAsync(MakeDict(docId, 22), [], Ct);

        // Delete only from storeA.
        await storeA.ApplyChangesAsync(new Dictionary<string, TState>(), [docId], Ct);

        (await storeA.LoadManyAsync([docId], Ct)).Should().BeEmpty("deleted from storeA");
        (await storeB.LoadManyAsync([docId], Ct))
            .Should().ContainKey(docId, "storeB's row must survive storeA's delete");
    }

    // ── Spec facts — rebuild-version scoping ──────────────────────────────────

    /// <summary>
    /// A store whose version selector returns N must not see rows written at version M (M≠N),
    /// even when both stores share the same projectionType.
    ///
    /// Uses a single store with a mutable version closure so the fact holds for InMemory
    /// (per-instance dictionary) and shared-storage adapters (Postgres, EF) alike.
    /// </summary>
    [Fact]
    public async Task RebuildVersion_WritesAtOneVersion_InvisibleAtAnother()
    {
        var projType = NewProjectionType();
        var version = 1;
        var store = await CreateStore(projType, () => version);
        var docId = $"doc-{TestId}-vis";

        await store.ApplyChangesAsync(MakeDict(docId, 10), [], Ct);

        version = 2;
        (await store.LoadManyAsync([docId], Ct)).Should().BeEmpty(
            "shadow version 2 starts from nothing");

        await store.ApplyChangesAsync(MakeDict(docId, 99), [], Ct);

        ReadValue((await store.LoadManyAsync([docId], Ct))[docId]).Should().Be(99);

        version = 1;
        ReadValue((await store.LoadManyAsync([docId], Ct))[docId])
            .Should().Be(10, "shadow rebuild must not overwrite what live readers see");
    }

    [Fact]
    public async Task RebuildVersion_Deletes_OnlyAffectCurrentVersion()
    {
        var projType = NewProjectionType();
        var version = 1;
        var store = await CreateStore(projType, () => version);
        var docId = $"doc-{TestId}-vdel";

        await store.ApplyChangesAsync(MakeDict(docId, 10), [], Ct);

        version = 2;
        await store.ApplyChangesAsync(MakeDict(docId, 99), [], Ct);
        await store.ApplyChangesAsync(new Dictionary<string, TState>(), [docId], Ct);

        (await store.LoadManyAsync([docId], Ct)).Should().BeEmpty("deleted at v2");

        version = 1;
        (await store.LoadManyAsync([docId], Ct))
            .Should().ContainKey(docId, "a delete in v2 must not remove the v1 row");
    }

    /// <summary>
    /// A store that resolves its version per operation automatically follows a promotion
    /// without being rebuilt — that is the property that makes zero-downtime rebuilds safe.
    /// </summary>
    [Fact]
    public async Task RebuildVersion_LongLivedStore_FollowsPromotion()
    {
        var projType = NewProjectionType();
        var version = 1;
        var store = await CreateStore(projType, () => version);
        var docId = $"doc-{TestId}-promo";

        await store.ApplyChangesAsync(MakeDict(docId, 10), [], Ct);  // live at v1

        version = 2; // shadow rebuild begins
        await store.ApplyChangesAsync(MakeDict(docId, 99), [], Ct);  // shadow at v2

        ReadValue((await store.LoadManyAsync([docId], Ct))[docId])
            .Should().Be(99, "store is now at v2 after promotion");

        version = 1; // verify v1 isolation: the live row was not overwritten
        ReadValue((await store.LoadManyAsync([docId], Ct))[docId])
            .Should().Be(10, "the shadow rebuild must not have overwritten the v1 row");
    }

    /// <summary>
    /// A store constructed without a version selector must behave as though its selector
    /// always returns 1.
    ///
    /// For adapters sharing a backing store (Postgres, EF) the cross-instance assertion
    /// proves this directly. For InMemory (per-instance dictionary,
    /// <see cref="SupportsSharedBackingStore"/> = false) we verify only that a probe store
    /// at version 2 sees nothing — which also passes because the probe's own dictionary
    /// is empty, not because it is looking at the right version bucket; the real
    /// default-is-1 invariant for InMemory is covered by
    /// <see cref="RebuildVersion_WritesAtOneVersion_InvisibleAtAnother"/>.
    /// </summary>
    [Fact]
    public async Task RebuildVersion_DefaultsToVersionOne()
    {
        var projType = NewProjectionType();
        var docId = $"doc-{TestId}-vdef";

        // A no-selector store — the shape used by almost every projection.
        var defaultStore = await CreateStore(projType);
        await defaultStore.ApplyChangesAsync(MakeDict(docId, 7), [], Ct);

        // Nothing at version 2 — holds for all adapters (different reason for InMemory).
        var version = 2;
        var probe = await CreateStore(projType, () => version);
        (await probe.LoadManyAsync([docId], Ct))
            .Should().BeEmpty("the no-selector store writes at version 1, not 2");

        // For adapters sharing a backing store, switch the probe to version 1 and confirm
        // the no-selector store wrote there.
        if (SupportsSharedBackingStore)
        {
            version = 1;
            ReadValue((await probe.LoadManyAsync([docId], Ct))[docId])
                .Should().Be(7, "the default version is 1");
        }
    }

    // ── Spec facts — tenant isolation ────────────────────────────────────────

    /// <summary>
    /// Two stores scoped to different <c>tenantId</c> values must not see each
    /// other's documents for the same <c>projectionType</c> and <c>documentId</c>.
    ///
    /// Skipped for adapters that do not support per-tenant scoping
    /// (<see cref="SupportsTenantIsolation"/> is false).
    /// </summary>
    [Fact]
    public async Task TenantId_Isolates_SameDocId()
    {
        if (!SupportsTenantIsolation)
            Assert.Skip(
                "This adapter does not support tenant-scoped stores. " +
                "Only the multi-tenant PostgresStateStore (constructed with a non-null " +
                "tenantId on a multi-tenant schema) provides tenant isolation.");

        var pt = NewProjectionType();
        var storeA = await CreateStoreForTenant(pt, $"tenant-A-{TestId}");
        var storeB = await CreateStoreForTenant(pt, $"tenant-B-{TestId}");
        var docId = $"doc-{TestId}-tenant-iso";

        await storeA.ApplyChangesAsync(MakeDict(docId, 11), [], Ct);
        await storeB.ApplyChangesAsync(MakeDict(docId, 22), [], Ct);

        var rA = await storeA.LoadManyAsync([docId], Ct);
        var rB = await storeB.LoadManyAsync([docId], Ct);

        ReadValue(rA[docId]).Should().Be(11, "storeA must not see storeB's data");
        ReadValue(rB[docId]).Should().Be(22, "storeB must not see storeA's data");
    }

    /// <summary>
    /// A delete on one tenant's store must not remove the same document from
    /// another tenant's store when both share the same <c>projectionType</c>.
    ///
    /// Skipped for adapters that do not support per-tenant scoping.
    /// </summary>
    [Fact]
    public async Task TenantId_DeleteInOne_DoesNotAffectOther()
    {
        if (!SupportsTenantIsolation)
            Assert.Skip(
                "This adapter does not support tenant-scoped stores; " +
                "see TenantId_Isolates_SameDocId for the full reason.");

        var pt = NewProjectionType();
        var storeA = await CreateStoreForTenant(pt, $"tenant-A-{TestId}");
        var storeB = await CreateStoreForTenant(pt, $"tenant-B-{TestId}");
        var docId = $"doc-{TestId}-tenant-del";

        await storeA.ApplyChangesAsync(MakeDict(docId, 11), [], Ct);
        await storeB.ApplyChangesAsync(MakeDict(docId, 22), [], Ct);

        await storeA.ApplyChangesAsync(new Dictionary<string, TState>(), [docId], Ct);

        (await storeA.LoadManyAsync([docId], Ct)).Should().BeEmpty("deleted from storeA");
        ReadValue((await storeB.LoadManyAsync([docId], Ct))[docId])
            .Should().Be(22, "storeB's row must survive storeA's delete");
    }

    // ── Spec facts — ListRecentAsync ──────────────────────────────────────────

    /// <summary>
    /// Values in the 1000-band belong to the <c>ListRecent_*</c> facts alone. An adapter that
    /// does not isolate by projectionType (EF) shares one table across every fact in the class,
    /// and <see cref="IStateStore{TState}.ListRecentAsync"/> takes no document IDs to filter by —
    /// so a fact asserting that a value is <em>absent</em> has to know no other fact writes it.
    /// </summary>
    private const int ListRecentValueBase = 1000;

    /// <summary>
    /// The documents come back most-recently-written first. Writes are in separate calls so
    /// each lands in its own transaction and gets its own timestamp; documents written in one
    /// batch share one and their relative order is not part of the contract.
    /// </summary>
    [Fact]
    public async Task ListRecent_ReturnsMostRecentlyWrittenFirst()
    {
        var store = await CreateStore(NewProjectionType());

        await store.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-1", ListRecentValueBase + 1), [], Ct);
        await store.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-2", ListRecentValueBase + 2), [], Ct);
        await store.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-3", ListRecentValueBase + 3), [], Ct);

        var recent = await store.ListRecentAsync(3, Ct);

        recent.Select(ReadValue).Should().Equal(
            ListRecentValueBase + 3, ListRecentValueBase + 2, ListRecentValueBase + 1);
    }

    /// <summary>
    /// A re-upsert of an existing document moves it back to the front — "recent" means last
    /// written, not first created.
    /// </summary>
    [Fact]
    public async Task ListRecent_ReUpsert_MovesDocumentToFront()
    {
        var store = await CreateStore(NewProjectionType());
        var firstId = $"doc-{TestId}-lr-touch-1";

        await store.ApplyChangesAsync(MakeDict(firstId, ListRecentValueBase + 11), [], Ct);
        await store.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-touch-2", ListRecentValueBase + 12), [], Ct);
        await store.ApplyChangesAsync(MakeDict(firstId, ListRecentValueBase + 13), [], Ct);

        var recent = await store.ListRecentAsync(2, Ct);

        ReadValue(recent[0]).Should().Be(ListRecentValueBase + 13);
    }

    [Fact]
    public async Task ListRecent_ReturnsAtMostLimitDocuments()
    {
        var store = await CreateStore(NewProjectionType());

        for (var i = 0; i < 3; i++)
            await store.ApplyChangesAsync(
                MakeDict($"doc-{TestId}-lr-limit-{i}", ListRecentValueBase + 20 + i), [], Ct);

        (await store.ListRecentAsync(2, Ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task ListRecent_ExcludesDeletedDocuments()
    {
        var store = await CreateStore(NewProjectionType());
        var dropId = $"doc-{TestId}-lr-drop";

        await store.ApplyChangesAsync(MakeDict(dropId, ListRecentValueBase + 31), [], Ct);
        await store.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-keep", ListRecentValueBase + 32), [], Ct);
        await store.ApplyChangesAsync(new Dictionary<string, TState>(), [dropId], Ct);

        var recent = await store.ListRecentAsync(20, Ct);

        recent.Select(ReadValue).Should()
            .Contain(ListRecentValueBase + 32).And
            .NotContain(ListRecentValueBase + 31);
    }

    /// <summary>
    /// <see cref="IStateStore{TState}.ListRecentAsync"/> is scoped exactly as
    /// <see cref="IStateStore{TState}.LoadManyAsync"/>: a shadow rebuild's documents stay
    /// invisible to a live reader, and vice versa. A list that leaked across versions would
    /// serve a half-built projection for the whole length of a rebuild.
    /// </summary>
    [Fact]
    public async Task ListRecent_ScopedToRebuildVersion()
    {
        var version = 1;
        var store = await CreateStore(NewProjectionType(), () => version);

        await store.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-v1", ListRecentValueBase + 41), [], Ct);

        version = 2;
        (await store.ListRecentAsync(20, Ct)).Select(ReadValue)
            .Should().NotContain(ListRecentValueBase + 41, "v1's documents are not v2's");

        await store.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-v2", ListRecentValueBase + 42), [], Ct);

        version = 1;
        var live = (await store.ListRecentAsync(20, Ct)).Select(ReadValue).ToList();
        live.Should().Contain(ListRecentValueBase + 41);
        live.Should().NotContain(ListRecentValueBase + 42, "the shadow's documents stay hidden");
    }

    /// <summary>
    /// A store lists only its own projectionType's documents. Without a document ID to filter
    /// on, this scoping is the only thing keeping one projection's list free of another's.
    ///
    /// Skipped for EF: <see cref="SupportsProjectionTypeIsolation"/> is false, so every store
    /// of the same TEntity lists one shared table.
    /// </summary>
    [Fact]
    public async Task ListRecent_ScopedToProjectionType()
    {
        if (!SupportsProjectionTypeIsolation)
            Assert.Skip(
                "EfStateStore<TEntity> isolates projections structurally via entity type " +
                "(table), so every store of the same TEntity lists the same rows; " +
                "see ProjectionType_Isolates_SameDocId for the full reason.");

        var storeA = await CreateStore(NewProjectionType());
        var storeB = await CreateStore(NewProjectionType());

        await storeA.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-pt-a", ListRecentValueBase + 51), [], Ct);
        await storeB.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-pt-b", ListRecentValueBase + 52), [], Ct);

        (await storeA.ListRecentAsync(20, Ct)).Select(ReadValue)
            .Should().Equal(ListRecentValueBase + 51);
        (await storeB.ListRecentAsync(20, Ct)).Select(ReadValue)
            .Should().Equal(ListRecentValueBase + 52);
    }

    /// <summary>
    /// A tenant-scoped store lists only that tenant's documents. This is the fact a resolver
    /// listing a per-tenant projection relies on: it passes a tenant and asks for "recent",
    /// with no per-document filter of its own to fall back on.
    ///
    /// Skipped for adapters that do not support per-tenant scoping.
    /// </summary>
    [Fact]
    public async Task ListRecent_ScopedToTenant()
    {
        if (!SupportsTenantIsolation)
            Assert.Skip(
                "This adapter does not support tenant-scoped stores; " +
                "see TenantId_Isolates_SameDocId for the full reason.");

        var pt = NewProjectionType();
        var storeA = await CreateStoreForTenant(pt, $"tenant-A-{TestId}");
        var storeB = await CreateStoreForTenant(pt, $"tenant-B-{TestId}");

        await storeA.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-tenant-a", ListRecentValueBase + 61), [], Ct);
        await storeB.ApplyChangesAsync(
            MakeDict($"doc-{TestId}-lr-tenant-b", ListRecentValueBase + 62), [], Ct);

        (await storeA.ListRecentAsync(20, Ct)).Select(ReadValue)
            .Should().Equal([ListRecentValueBase + 61], "storeA must not list storeB's documents");
        (await storeB.ListRecentAsync(20, Ct)).Select(ReadValue)
            .Should().Equal([ListRecentValueBase + 62], "storeB must not list storeA's documents");
    }

    /// <summary>
    /// An empty scope lists nothing rather than throwing.
    ///
    /// Skipped for EF, where no store has a scope of its own to be empty
    /// (<see cref="SupportsProjectionTypeIsolation"/> is false).
    /// </summary>
    [Fact]
    public async Task ListRecent_EmptyScope_ReturnsEmpty()
    {
        if (!SupportsProjectionTypeIsolation)
            Assert.Skip(
                "EfStateStore<TEntity> lists one table shared with every other store of the " +
                "same TEntity, so a fresh store's scope is not empty.");

        var store = await CreateStore(NewProjectionType());

        (await store.ListRecentAsync(20, Ct)).Should().BeEmpty();
    }

    // ── Spec fact — concurrent access ─────────────────────────────────────────

    [Fact]
    public async Task ConcurrentApplyChanges_SameDocId_ResultsInOneWinner()
    {
        var store = await CreateStore(NewProjectionType());
        var docId = $"doc-{TestId}-race";

        // Fire two concurrent writes and let the adapters resolve the conflict.
        // InMemory: ConcurrentDictionary.AddOrUpdate is thread-safe; last write wins.
        // Postgres: ON CONFLICT DO UPDATE serialises both; last commit wins.
        // EF: unique-constraint retry loop resolves the conflict; one of the two values wins.
        await Task.WhenAll(
            store.ApplyChangesAsync(MakeDict(docId, 1), [], Ct),
            store.ApplyChangesAsync(MakeDict(docId, 2), [], Ct));

        var result = await store.LoadManyAsync([docId], Ct);
        result.Should().ContainKey(docId, "at least one concurrent write must have committed");
        ReadValue(result[docId]).Should().BeOneOf(1, 2);
    }
}

// ---------------------------------------------------------------------------
// InMemory adapter
// ---------------------------------------------------------------------------

/// <summary>
/// Runs <see cref="StateStoreSpecification{TState}"/> against
/// <see cref="InMemoryStateStore{TState}"/>.
/// </summary>
public sealed class InMemoryStateStoreSpecificationTests : StateStoreSpecification<SimpleState>
{
    /// <summary>
    /// InMemory stores are not composable: each instance owns a private dictionary, so
    /// two store instances never share state regardless of projectionType.
    /// The rebuild-version facts that require a cross-instance read
    /// (<see cref="StateStoreSpecification{TState}.RebuildVersion_DefaultsToVersionOne"/>)
    /// fall back to a weaker assertion for this adapter; version isolation behaviour is
    /// fully covered by the single-instance facts that drive version changes through a
    /// mutable closure.
    /// </summary>
    protected override bool SupportsSharedBackingStore => false;

    protected override Task<IStateStore<SimpleState>> CreateStore(
        string projectionType,
        Func<int>? rebuildVersion = null) =>
        Task.FromResult<IStateStore<SimpleState>>(
            new InMemoryStateStore<SimpleState>(rebuildVersion));

    protected override SimpleState MakeState(int value) => new() { Value = value };
    protected override int ReadValue(SimpleState state) => state.Value;
}

// ---------------------------------------------------------------------------
// Postgres adapter
// ---------------------------------------------------------------------------

/// <summary>
/// Runs <see cref="StateStoreSpecification{TState}"/> against
/// <see cref="PostgresStateStore{TState}"/> (single-tenant schema).
///
/// <para>
/// The tests that previously lived in <c>PostgresStateStoreTests</c> under the name
/// "TenantIsolation_*" were actually testing projectionType isolation: the helper
/// <c>CreateStore&lt;T&gt;(string?)</c> passed its argument as the <c>projectionType</c>
/// constructor parameter (position 2), not as <c>tenantId</c> (position 5). Those facts
/// are now correctly named and covered by
/// <see cref="StateStoreSpecification{TState}.ProjectionType_Isolates_SameDocId"/>.
/// </para>
/// <para>
/// The real multi-tenant mode (constructing <see cref="PostgresStateStore{TState}"/> with
/// a non-null <c>tenantId</c>) is covered by
/// <see cref="MultiTenantPostgresStateStoreSpecificationTests"/>, which runs the same
/// spec facts against the multi-tenant schema and additionally exercises tenant-isolation
/// semantics via <see cref="StateStoreSpecification{TState}.SupportsTenantIsolation"/>.
/// </para>
/// </summary>
public sealed class PostgresStateStoreSpecificationTests(SingleTenantPostgresFixture fixture)
    : StateStoreSpecification<SimpleState>, IClassFixture<SingleTenantPostgresFixture>
{
    protected override Task<IStateStore<SimpleState>> CreateStore(
        string projectionType,
        Func<int>? rebuildVersion = null) =>
        Task.FromResult<IStateStore<SimpleState>>(
            new PostgresStateStore<SimpleState>(
                fixture.DataSource,
                projectionType,
                rebuildVersion: rebuildVersion));

    protected override SimpleState MakeState(int value) => new() { Value = value };
    protected override int ReadValue(SimpleState state) => state.Value;
}

// ---------------------------------------------------------------------------
// EF adapter
// ---------------------------------------------------------------------------

/// <summary>
/// Runs <see cref="StateStoreSpecification{TState}"/> against
/// <see cref="EfStateStore{TEntity,TDbContext}"/> using <see cref="CounterEntity"/>
/// and <see cref="EfTestDbContext"/>.
///
/// <para>
/// <see cref="StateStoreSpecification{TState}.SupportsProjectionTypeIsolation"/> is
/// <c>false</c>: <c>EfStateStore&lt;CounterEntity&gt;</c> always writes to
/// <c>ef_test_counters</c> regardless of the projectionType string the spec seam
/// supplies. Separation between projections is structural (different TEntity types →
/// different tables), not a runtime string discriminator.
/// </para>
/// <para>
/// All spec tests in this class share one table, so every docId includes
/// <see cref="StateStoreSpecification{TState}.TestId"/> to prevent cross-test
/// interference.
/// </para>
/// </summary>
public sealed class EfStateStoreSpecificationTests(EfProjectionTestFixture fixture)
    : StateStoreSpecification<CounterEntity>, IClassFixture<EfProjectionTestFixture>
{
    protected override bool SupportsProjectionTypeIsolation => false;

    protected override Task<IStateStore<CounterEntity>> CreateStore(
        string projectionType,
        Func<int>? rebuildVersion = null) =>
        // projectionType is accepted to satisfy the spec seam but is not forwarded to
        // EfStateStore — it has no such parameter.
        Task.FromResult<IStateStore<CounterEntity>>(
            new EfStateStore<CounterEntity, EfTestDbContext>(
                fixture.CreateFactory(),
                rebuildVersion));

    protected override CounterEntity MakeState(int value) => new() { Counter = value };
    protected override int ReadValue(CounterEntity state) => state.Counter;
}

// ---------------------------------------------------------------------------
// Postgres multi-tenant adapter
// ---------------------------------------------------------------------------

/// <summary>
/// Runs <see cref="StateStoreSpecification{TState}"/> against
/// <see cref="PostgresStateStore{TState}"/> on the <em>multi-tenant</em> schema
/// — the one that includes the <c>tenant_id</c> column in
/// <c>alberto_projection_states</c>.
///
/// <para>
/// Every store here is constructed <em>with</em> a <c>tenantId</c>, because on this schema
/// there is no other option: <c>tenant_id</c> is <c>NOT NULL</c> and part of the primary key,
/// so a store built without one names an <c>ON CONFLICT</c> constraint that does not exist and
/// fails every write with <c>42P10</c>. That is why a cross-tenant projection on a tenant-enabled
/// module stores its single document under <c>TenantScope.CrossTenant</c> rather than under no
/// tenant at all — see <see cref="CrossTenantProjectionContractTests"/>.
/// </para>
/// <para>
/// <see cref="StateStoreSpecification{TState}.SupportsTenantIsolation"/> is
/// <c>true</c>: stores constructed <em>with</em> a <c>tenantId</c> via
/// <see cref="StateStoreSpecification{TState}.CreateStoreForTenant"/> must
/// isolate data between tenants.
/// </para>
/// </summary>
public sealed class MultiTenantPostgresStateStoreSpecificationTests(MultiTenantDbFixture fixture)
    : StateStoreSpecification<SimpleState>, IClassFixture<MultiTenantDbFixture>
{
    protected override bool SupportsTenantIsolation => true;

    /// <summary>
    /// Creates a store scoped to a per-test, fixed tenant. The multi-tenant schema
    /// enforces <c>tenant_id NOT NULL</c>, so a store without <c>tenantId</c> cannot
    /// write. Using a fixed per-test tenant lets the base-spec facts (upsert, delete,
    /// rebuild-version scoping, etc.) run against the multi-tenant schema without
    /// modification.
    /// </summary>
    protected override Task<IStateStore<SimpleState>> CreateStore(
        string projectionType,
        Func<int>? rebuildVersion = null) =>
        Task.FromResult<IStateStore<SimpleState>>(
            new PostgresStateStore<SimpleState>(
                fixture.DataSource,
                projectionType,
                rebuildVersion: rebuildVersion,
                tenantId: $"spec-tenant-{TestId}"));

    /// <summary>
    /// Creates a store scoped to an explicit <paramref name="tenantId"/> on the multi-tenant
    /// schema. Called by the <c>TenantId_*</c> isolation facts, which pass two different
    /// tenantId values to verify that documents are isolated between tenants.
    /// </summary>
    protected override Task<IStateStore<SimpleState>> CreateStoreForTenant(
        string projectionType,
        string tenantId,
        Func<int>? rebuildVersion = null) =>
        Task.FromResult<IStateStore<SimpleState>>(
            new PostgresStateStore<SimpleState>(
                fixture.DataSource,
                projectionType,
                rebuildVersion: rebuildVersion,
                tenantId: tenantId));

    protected override SimpleState MakeState(int value) => new() { Value = value };
    protected override int ReadValue(SimpleState state) => state.Value;
}
