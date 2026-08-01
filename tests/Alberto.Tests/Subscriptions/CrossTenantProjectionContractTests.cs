using Alberto.Postgres;
using Alberto.Tenancy;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Documents and verifies the projection tenancy contract on a <em>single-tenant</em> schema:
/// a store's tenancy mode is decided by the schema it points at, not by the caller's intent,
/// so a reader and a writer that disagree about it cannot exchange data.
///
/// <para>
/// <strong>Historical bug (fixed):</strong>
/// <c>OrdersOverviewQuery.GetOrdersOverview</c> and <c>PaymentsOverviewQuery.GetPaymentsOverview</c>
/// constructed their reader stores <em>with</em> a <c>tenantId:</c> argument while the
/// writers (<c>OrdersModule</c> / <c>PaymentsModule</c>) constructed theirs
/// <em>without</em> one. Because <see cref="PostgresStateStore{TState}"/> generates
/// different SQL in each mode, the reader's <c>WHERE tenant_id = @tenant_id</c> clause
/// referenced a column the single-tenant schema does not have, and the query threw.
/// </para>
///
/// <para>
/// <strong>Fix:</strong> reader and writer no longer decide independently.
/// <see cref="Alberto.DcbModuleBuilderExtensions.AddProjection{TState}"/> registers the
/// writer's own <c>Func&lt;string?, IStateStore&lt;TState&gt;&gt;</c> keyed by
/// <c>"{moduleKey}:{declaration.ProcessorId}"</c>, and readers resolve it rather than
/// constructing a store of their own — leaving them nothing to disagree about except which
/// tenant to read.
/// </para>
///
/// <para>
/// The mirror-image contract on a tenant-enabled schema — where the failure is <c>42P10</c>
/// rather than <c>42703</c>, and where a cross-tenant aggregate has to be stored under
/// <c>TenantScope.CrossTenant</c> because there is no tenant-less row to store it as — is
/// covered by <see cref="CrossTenantProjectionOnMultiTenantSchemaTests"/>.
/// </para>
/// </summary>
public sealed class CrossTenantProjectionContractTests(SingleTenantPostgresFixture fixture)
    : IClassFixture<SingleTenantPostgresFixture>
{
    private readonly string _id = Guid.NewGuid().ToString("N")[..8];
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A writer without <c>tenantId</c> and a reader without <c>tenantId</c> share
    /// the same primary key; data written by the writer must be visible to the reader.
    ///
    /// <para>
    /// This is the regression test for the fixed behaviour. Before the fix the reader
    /// was constructed <em>with</em> <c>tenantId: "some-tenant"</c> — mirroring
    /// <c>OrdersOverviewQuery.GetOrdersOverview</c> — which on the single-tenant schema threw
    /// <c>PostgresException</c> ("column tenant_id does not exist"), causing the test
    /// to fail before the assertion was ever reached.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CrossTenantProjection_ReaderWithoutTenantId_SeesWhatWriterWrote()
    {
        var projectionType = $"ct-contract-{_id}";
        var docId = "overview";

        // Writer path: no tenantId — the only mode this schema supports, and the one a
        // module that never called .WithTenancy() gets.
        var writer = new PostgresStateStore<SimpleState>(fixture.DataSource, projectionType);
        await writer.ApplyChangesAsync(
            new Dictionary<string, SimpleState> { [docId] = new SimpleState { Value = 42 } },
            [],
            Ct);

        // Reader path: also no tenantId (fixed).
        // BEFORE FIX this line read:
        //   new PostgresStateStore<SimpleState>(fixture.DataSource, projectionType, tenantId: "some-tenant")
        // That threw PostgresException on the single-tenant schema because the tenant_id
        // column does not exist.
        var reader = new PostgresStateStore<SimpleState>(fixture.DataSource, projectionType);
        var result = await reader.LoadManyAsync([docId], Ct);

        result.Should().ContainKey(docId,
            "a cross-tenant reader must see what the cross-tenant writer stored");
        result[docId].Value.Should().Be(42);
    }

    /// <summary>
    /// A reader constructed <em>with</em> a <c>tenantId</c> argument on a single-tenant
    /// schema throws immediately, pinning the pre-fix failure mode as a regression guard.
    ///
    /// <para>
    /// If <c>tenantId:</c> is re-introduced for a reader that points at a single-tenant
    /// schema, the query fails loudly with a schema error rather than silently returning
    /// an empty collection — preserving the loud signal while the correct fix is to
    /// remove <c>tenantId:</c> entirely.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CrossTenantProjection_MultiTenantReaderOnSingleTenantSchema_Throws()
    {
        var projectionType = $"ct-mismatch-{_id}";
        var docId = "overview";

        var writer = new PostgresStateStore<SimpleState>(fixture.DataSource, projectionType);
        await writer.ApplyChangesAsync(
            new Dictionary<string, SimpleState> { [docId] = new SimpleState { Value = 1 } },
            [],
            Ct);

        // Multi-tenant SQL ("WHERE tenant_id = ...") on a schema with no tenant_id column.
        var reader = new PostgresStateStore<SimpleState>(
            fixture.DataSource, projectionType, tenantId: "some-tenant");

        // Pin the exact failure, not just "something threw": a bare ThrowAsync<Exception>
        // would also be satisfied by an unrelated ArgumentException and would stop
        // detecting the writer/reader schema disagreement this test exists to guard.
        var act = () => reader.LoadManyAsync([docId], Ct);
        await act.Should().ThrowAsync<PostgresException>(
                "a multi-tenant reader on a single-tenant schema must fail fast")
            .Where(e => e.SqlState == "42703",
                "the failure must be the missing tenant_id column (undefined_column)");
    }
}

/// <summary>
/// The same contract on a <em>tenant-enabled</em> schema, which is the one
/// <c>OrdersModule</c> and <c>PaymentsModule</c> actually run against — both call
/// <c>.WithTenancy()</c>.
///
/// <para>
/// <strong>Historical bug (fixed):</strong> both modules registered their JSONB projections
/// with a store built <em>without</em> a <c>tenantId</c>, on the theory that a cross-tenant
/// aggregate belongs to no tenant. On this schema <c>tenant_id</c> is <c>NOT NULL</c> and part
/// of the primary key, so that store's <c>ON CONFLICT (projection_type, document_id,
/// rebuild_version)</c> names a constraint that does not exist and <em>every</em> write fails
/// with <c>42P10</c>. The projections had never written a row: <c>getPaymentsOverview</c> and
/// <c>getRecentPayments</c> returned nothing because the table was empty, and the dead-lettered
/// writes were the only trace.
/// </para>
///
/// <para>
/// <strong>Fix:</strong> a store on this schema always carries a tenant. A per-tenant projection
/// gets the tenant of the events it is given; a cross-tenant aggregate gets
/// <c>TenantScope.CrossTenant</c> — a real tenant value that no tenant can be called, so the
/// single blended document has a row to live in without colliding with anyone's.
/// </para>
/// </summary>
public sealed class CrossTenantProjectionOnMultiTenantSchemaTests(MultiTenantDbFixture fixture)
    : IClassFixture<MultiTenantDbFixture>
{
    private readonly string _id = Guid.NewGuid().ToString("N")[..8];
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The pre-fix registration, pinned: a store built without a <c>tenantId</c> against a
    /// tenant-enabled schema cannot write at all. Nothing about the failure is subtle once it
    /// is visible — it is only invisible because the control loop dead-letters it while the
    /// rest of the module keeps running.
    /// </summary>
    [Fact]
    public async Task WriterWithoutTenantId_OnTenantEnabledSchema_FailsEveryWrite()
    {
        var writer = new PostgresStateStore<SimpleState>(fixture.DataSource, $"mt-nowrite-{_id}");

        var act = () => writer.ApplyChangesAsync(
            new Dictionary<string, SimpleState> { ["overview"] = new() { Value = 1 } }, [], Ct);

        await act.Should().ThrowAsync<PostgresException>(
                "a tenant-less store cannot write to a tenant-keyed table")
            .Where(e => e.SqlState == "42P10",
                "the ON CONFLICT target it names is not a constraint on this schema");
    }

    /// <summary>
    /// A cross-tenant aggregate stored under <c>TenantScope.CrossTenant</c> round-trips, and
    /// the sentinel is what both sides agree on: a reader passing a real tenant sees nothing.
    /// </summary>
    [Fact]
    public async Task CrossTenantAggregate_RoundTripsUnderTheSentinel()
    {
        var projectionType = $"mt-sentinel-{_id}";
        var docId = "overview";

        var writer = new PostgresStateStore<SimpleState>(
            fixture.DataSource, projectionType, tenantId: TenantScope.CrossTenant);
        await writer.ApplyChangesAsync(
            new Dictionary<string, SimpleState> { [docId] = new() { Value = 42 } }, [], Ct);

        var reader = new PostgresStateStore<SimpleState>(
            fixture.DataSource, projectionType, tenantId: TenantScope.CrossTenant);
        var found = await reader.LoadManyAsync([docId], Ct);

        found.Should().ContainKey(docId);
        found[docId].Value.Should().Be(42);

        var tenantReader = new PostgresStateStore<SimpleState>(
            fixture.DataSource, projectionType, tenantId: $"tenant-{_id}");
        (await tenantReader.LoadManyAsync([docId], Ct)).Should().BeEmpty(
            "the blended document belongs to the sentinel, not to any one tenant");
    }

    /// <summary>
    /// <c>TenantScope.CrossTenantFor</c> is the shape a registration uses: it keeps the store
    /// tenant-less when the module has no tenancy (a <c>null</c> arriving from the events) and
    /// substitutes the sentinel when it does. Getting this wrong in either direction is the
    /// 42P10/42703 pair, so the mapping itself is worth pinning.
    /// </summary>
    [Fact]
    public void CrossTenantFor_MapsNullToNull_AndAnyTenantToTheSentinel()
    {
        TenantScope.CrossTenantFor(null).Should().BeNull();
        TenantScope.CrossTenantFor("acme").Should().Be(TenantScope.CrossTenant);
        TenantScope.CrossTenantFor(TenantScope.CrossTenant).Should().Be(TenantScope.CrossTenant);
    }
}
