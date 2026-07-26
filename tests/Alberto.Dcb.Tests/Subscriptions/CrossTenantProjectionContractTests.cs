using Alberto.Dcb.Postgres;
using Alberto.Dcb.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Documents and verifies the cross-tenant projection contract:
/// a writer constructed without <c>tenantId</c> and a reader constructed without
/// <c>tenantId</c> share the same primary key and must be able to exchange data.
///
/// <para>
/// <strong>Historical bug (fixed):</strong>
/// <c>OrderQueries.GetOrdersOverview</c> and <c>PaymentQueries.CreateStateStore</c>
/// constructed their reader stores <em>with</em> a <c>tenantId:</c> argument while the
/// writers (<c>OrdersModule</c> / <c>PaymentsModule</c>) constructed theirs
/// <em>without</em> one. Because <see cref="PostgresStateStore{TState}"/> generates
/// different SQL in each mode, the data written by the writer was invisible to the reader:
/// on a single-tenant schema the reader's <c>WHERE tenant_id = @tenant_id</c> clause
/// references a column that does not exist (immediate exception); on a multi-tenant schema
/// the writer's INSERT omits the NOT NULL <c>tenant_id</c> column (immediate exception).
/// Either way <c>getOrdersOverview</c>, <c>getPaymentsOverview</c>, and
/// <c>getRecentPayments</c> returned nothing.
/// </para>
///
/// <para>
/// <strong>Fix:</strong> readers for cross-tenant aggregates are constructed
/// <em>without</em> a <c>tenantId</c> argument, matching the writer. To prevent
/// future disagreement, <see cref="Alberto.Dcb.DcbModuleBuilderExtensions.AddProjection{TState}"/>
/// now registers a <c>Func&lt;IStateStore&lt;TState&gt;&gt;</c> keyed by
/// <c>"{moduleKey}:{declaration.ProcessorId}"</c>; readers resolve their store
/// factory from DI rather than constructing one independently.
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
    /// <c>OrderQueries.GetOrdersOverview</c> — which on the single-tenant schema threw
    /// <c>PostgresException</c> ("column tenant_id does not exist"), causing the test
    /// to fail before the assertion was ever reached.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CrossTenantProjection_ReaderWithoutTenantId_SeesWhatWriterWrote()
    {
        var projectionType = $"ct-contract-{_id}";
        var docId = "overview";

        // Writer path: no tenantId — the same pattern used by OrdersModule and
        // PaymentsModule for cross-tenant aggregate projections.
        var writer = new PostgresStateStore<SimpleState>(fixture.DataSource, projectionType);
        await writer.ApplyChangesAsync(
            new Dictionary<string, SimpleState> { [docId] = new SimpleState { Value = 42 } },
            [],
            Ct);

        // Reader path: also no tenantId (fixed).
        // BEFORE FIX this line read:
        //   new PostgresStateStore<SimpleState>(fixture.DataSource, projectionType, tenantId: "some-tenant")
        // That threw PostgresException on the single-tenant schema because the tenant_id
        // column does not exist; on a multi-tenant schema the writer's INSERT itself
        // failed (tenant_id NOT NULL). Either way the reader returned nothing.
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
