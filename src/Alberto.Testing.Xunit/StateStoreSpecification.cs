using Alberto.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Testing.Xunit;

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
///
/// Derive from this class, implement the factory methods, and set the
/// capability hooks to run Alberto's own state-store conformance suite against
/// your implementation.
/// </summary>
public abstract class StateStoreSpecification<TState>
{
    /// <summary>
    /// Short identifier that makes document IDs unique across concurrent test runs.
    /// </summary>
    protected string TestId { get; } = Guid.NewGuid().ToString("N")[..8];

    // ── Capability hooks ─────────────────────────────────────────────────────

    /// <summary>
    /// True when two <c>CreateStore</c> calls with different <c>projectionType</c>
    /// strings are isolated from each other.
    ///
    /// InMemory and Postgres: <c>true</c> — projectionType is a first-class discriminator.
    /// EF: <c>false</c> — isolation is structural (entity type → table); the projectionType
    /// parameter is accepted by the spec seam but is not forwarded to
    /// <c>EfStateStore&lt;TEntity, TDbContext&gt;</c>, so two stores of the same TEntity
    /// always share one table.
    /// </summary>
    protected virtual bool SupportsProjectionTypeIsolation => true;

    /// <summary>
    /// True when two store instances created with the same <c>projectionType</c>
    /// share a backing store (database, etc.) so reads from one see writes from the other.
    ///
    /// InMemory: <c>false</c> — each instance owns a private dictionary.
    /// Postgres and EF: <c>true</c> — both point at the same database.
    /// </summary>
    protected virtual bool SupportsSharedBackingStore => true;

    /// <summary>
    /// True when two stores created with different <c>tenantId</c> values for the same
    /// <c>projectionType</c> are isolated from each other.
    ///
    /// Only the multi-tenant <c>PostgresStateStore&lt;TState&gt;</c> (constructed with
    /// a non-null <c>tenantId</c> on a multi-tenant schema) implements this.
    /// InMemory and EF: <c>false</c>.
    /// </summary>
    protected virtual bool SupportsTenantIsolation => false;

    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a store scoped to <paramref name="projectionType"/>, optionally
    /// with a custom rebuild-version handle.
    /// </summary>
    /// <param name="projectionType">The projection type name used as a scope discriminator.</param>
    /// <param name="rebuildVersion">
    /// Optional live version handle. Defaults to <see cref="ProjectionVersion.NeverRebuilt"/>.
    /// </param>
    protected abstract Task<IStateStore<TState>> CreateStore(
        string projectionType,
        ProjectionVersion rebuildVersion = default);

    /// <summary>
    /// Creates a store scoped to both <paramref name="projectionType"/> and
    /// <paramref name="tenantId"/>. Only called when
    /// <see cref="SupportsTenantIsolation"/> is <c>true</c>; subclasses that
    /// set it to <c>true</c> must override this.
    /// </summary>
    /// <param name="projectionType">The projection type name used as a scope discriminator.</param>
    /// <param name="tenantId">The tenant identifier to scope the store to.</param>
    /// <param name="rebuildVersion">
    /// Optional live version handle. Defaults to <see cref="ProjectionVersion.NeverRebuilt"/>.
    /// </param>
    protected virtual Task<IStateStore<TState>> CreateStoreForTenant(
        string projectionType,
        string tenantId,
        ProjectionVersion rebuildVersion = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support tenant-scoped stores " +
            "(SupportsTenantIsolation is false).");

    /// <summary>Returns a distinguishable state value carrying <paramref name="value"/>.</summary>
    /// <param name="value">The integer value to embed in the state object.</param>
    protected abstract TState MakeState(int value);

    /// <summary>Extracts the distinguishing value from a state instance.</summary>
    /// <param name="state">The state object to extract the value from.</param>
    protected abstract int ReadValue(TState state);

    /// <summary>Generates a fresh projection-type string per invocation.</summary>
    protected string NewProjectionType() =>
        $"spec-{TestId}-{Guid.NewGuid():N}";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Dictionary<string, TState> MakeDict(string docId, int value) =>
        new() { [docId] = MakeState(value) };

    // ── Spec facts — core read/write contract ─────────────────────────────────

    /// <summary>An empty ID list must return an empty dictionary without throwing.</summary>
    [Fact]
    public async Task LoadMany_EmptyIds_ReturnsEmptyDictionary()
    {
        var store = await CreateStore(NewProjectionType());

        var result = await store.LoadManyAsync([], Ct);

        result.Should().BeEmpty();
    }

    /// <summary>IDs that do not exist in the store must be absent from the result (no KeyNotFoundException).</summary>
    [Fact]
    public async Task LoadMany_NonExistentIds_ReturnsEmptyDictionary()
    {
        var store = await CreateStore(NewProjectionType());

        var result = await store.LoadManyAsync(
            [$"missing-{TestId}-a", $"missing-{TestId}-b"], Ct);

        result.Should().BeEmpty();
    }

    /// <summary>After an upsert, <c>LoadManyAsync</c> must return the stored state for the document ID.</summary>
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

    /// <summary>A second upsert for the same document ID must overwrite the previous value.</summary>
    [Fact]
    public async Task ApplyChanges_SecondUpsert_OverwritesPreviousValue()
    {
        var store = await CreateStore(NewProjectionType());
        var docId = $"doc-{TestId}-overwrite";

        await store.ApplyChangesAsync(MakeDict(docId, 10), [], Ct);
        await store.ApplyChangesAsync(MakeDict(docId, 20), [], Ct);

        ReadValue((await store.LoadManyAsync([docId], Ct))[docId]).Should().Be(20);
    }

    /// <summary>Deleting a document ID must remove it so subsequent loads return an empty result.</summary>
    [Fact]
    public async Task ApplyChanges_Delete_RemovesDocument()
    {
        var store = await CreateStore(NewProjectionType());
        var docId = $"doc-{TestId}-delete";

        await store.ApplyChangesAsync(MakeDict(docId, 1), [], Ct);
        await store.ApplyChangesAsync(new Dictionary<string, TState>(), [docId], Ct);

        (await store.LoadManyAsync([docId], Ct)).Should().BeEmpty();
    }

    /// <summary>Deleting a document ID that does not exist must not throw.</summary>
    [Fact]
    public async Task ApplyChanges_DeleteNonExistent_DoesNotThrow()
    {
        var store = await CreateStore(NewProjectionType());

        var act = async () =>
            await store.ApplyChangesAsync(
                new Dictionary<string, TState>(), [$"doc-{TestId}-absent"], Ct);

        await act.Should().NotThrowAsync();
    }

    /// <summary>An empty upsert-and-delete batch must not throw.</summary>
    [Fact]
    public async Task ApplyChanges_EmptyBatch_DoesNotThrow()
    {
        var store = await CreateStore(NewProjectionType());

        var act = async () =>
            await store.ApplyChangesAsync(new Dictionary<string, TState>(), [], Ct);

        await act.Should().NotThrowAsync();
    }

    /// <summary>A batch that both upserts and deletes must apply all changes atomically.</summary>
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

    /// <summary>Loading a mix of existing and missing IDs must return only the existing documents.</summary>
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
        var store = await CreateStore(projType, ProjectionVersion.From(() => version));
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

    /// <summary>A delete at one rebuild version must not remove the document at another version.</summary>
    [Fact]
    public async Task RebuildVersion_Deletes_OnlyAffectCurrentVersion()
    {
        var projType = NewProjectionType();
        var version = 1;
        var store = await CreateStore(projType, ProjectionVersion.From(() => version));
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
        var store = await CreateStore(projType, ProjectionVersion.From(() => version));
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
        var probe = await CreateStore(projType, ProjectionVersion.From(() => version));
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

    /// <summary>The result must contain at most <c>limit</c> documents.</summary>
    [Fact]
    public async Task ListRecent_ReturnsAtMostLimitDocuments()
    {
        var store = await CreateStore(NewProjectionType());

        for (var i = 0; i < 3; i++)
            await store.ApplyChangesAsync(
                MakeDict($"doc-{TestId}-lr-limit-{i}", ListRecentValueBase + 20 + i), [], Ct);

        (await store.ListRecentAsync(2, Ct)).Should().HaveCount(2);
    }

    /// <summary>Deleted documents must not appear in the listing.</summary>
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
        var store = await CreateStore(NewProjectionType(), ProjectionVersion.From(() => version));

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

    /// <summary>
    /// Two concurrent writes to the same document ID must both eventually commit; the result
    /// must be one of the two values (one wins, neither is lost from the store's perspective,
    /// and no exception escapes).
    /// </summary>
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

    // ── Spec facts — ApplyChanges atomicity ───────────────────────────────────

    /// <summary>
    /// When the same document ID appears in both <c>upserts</c> and <c>deletes</c>, the
    /// document must be absent after the batch — delete wins over upsert within one batch.
    /// Both adapters run upserts before deletes inside the same transaction or lock region,
    /// so the delete always executes last and the document cannot be accidentally resurrected.
    /// </summary>
    [Fact]
    public async Task ApplyChanges_SameDocument_InUpsertsAndDeletes_DeleteWins()
    {
        var store = await CreateStore(NewProjectionType());
        var docId = $"doc-{TestId}-delete-wins";

        await store.ApplyChangesAsync(
            new Dictionary<string, TState> { [docId] = MakeState(99) },
            [docId],
            Ct);

        (await store.LoadManyAsync([docId], Ct)).Should().BeEmpty(
            "delete wins when the same document ID appears in both upserts and deletes");
    }

    /// <summary>
    /// A concurrent reader must never observe a batch half-applied: each read through the store
    /// sees either the complete before-state or the complete after-state, never a mix of both.
    /// The test is bounded by iteration count and structured so it fails deterministically
    /// against an unsynchronised implementation — two document sets are swapped back and forth
    /// in one thread while another thread asserts each observed snapshot is consistent.
    /// </summary>
    [Fact]
    public async Task ApplyChanges_IsNotObservedPartiallyApplied()
    {
        const int n = 20;
        const int writerIterations = 300;

        var store = await CreateStore(NewProjectionType());

        var beforeIds = Enumerable.Range(0, n)
            .Select(i => $"doc-{TestId}-atom-before-{i}").ToArray();
        var afterIds = Enumerable.Range(0, n)
            .Select(i => $"doc-{TestId}-atom-after-{i}").ToArray();
        var allIds = beforeIds.Concat(afterIds).ToList();

        IReadOnlyDictionary<string, TState> beforeUpserts =
            beforeIds.ToDictionary(id => id, _ => MakeState(1));
        IReadOnlyDictionary<string, TState> afterUpserts =
            afterIds.ToDictionary(id => id, _ => MakeState(2));

        // Seed the initial "before" state so the reader has something to see from the start.
        await store.ApplyChangesAsync(beforeUpserts, [], Ct);

        // Written by the reader, read by both — Volatile rather than a plain bool because the
        // two loops run on different thread-pool threads and neither takes a lock.
        var partial = 0;

        // Capture the cancellation token before entering Task.Run — TestContext.Current is
        // thread-local and not available on the thread pool threads.
        var ct = Ct;

        // The reader runs until the writer stops rather than for a fixed count: against a
        // remote adapter the two loops advance at very different rates, and a reader that
        // outlasts the writer only re-reads a store nothing is changing any more.
        using var writerDone = new CancellationTokenSource();

        var writer = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < writerIterations && Volatile.Read(ref partial) == 0; i++)
                {
                    // Flip the store from "before" documents to "after" documents in one batch.
                    // An unsynchronised implementation exposes a window between the upsert loop
                    // (which adds the "after" set) and the delete loop (which removes the
                    // "before" set) where a reader observes both sets simultaneously.
                    await store.ApplyChangesAsync(afterUpserts, beforeIds, ct);
                    await store.ApplyChangesAsync(beforeUpserts, afterIds, ct);
                }
            }
            finally
            {
                await writerDone.CancelAsync();
            }
        });

        var reader = Task.Run(async () =>
        {
            while (!writerDone.IsCancellationRequested && Volatile.Read(ref partial) == 0)
            {
                var result = await store.LoadManyAsync(allIds, ct);
                var hasBefore = beforeIds.Any(result.ContainsKey);
                var hasAfter = afterIds.Any(result.ContainsKey);

                // A consistent snapshot contains documents from exactly one of the two sets.
                // Seeing both simultaneously is the partial-apply defect this fact guards.
                if (hasBefore && hasAfter)
                    Volatile.Write(ref partial, 1);
            }
        });

        await Task.WhenAll(writer, reader);

        (partial == 0).Should().BeTrue(
            "a concurrent read must see either the complete before-state or the complete " +
            "after-state, never documents from both sets at once");
    }
}
