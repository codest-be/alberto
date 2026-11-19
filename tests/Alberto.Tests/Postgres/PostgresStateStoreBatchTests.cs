using System.Text;
using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// The set-based write (one <c>unnest</c> upsert plus one <c>ANY</c> delete per batch) and the
/// <c>ListRecentAsync</c> index from migration 012, in both tenancy modes.
/// </summary>
public abstract class PostgresStateStoreBatchTests(NpgsqlDataSource dataSource, string? tenantId)
{
    private sealed record Doc(int Value);

    private readonly string _type = $"batch-{Guid.NewGuid():N}";

    private PostgresStateStore<Doc> Store(string? type = null, string? tenant = null) =>
        new(dataSource, type ?? _type, tenantId: tenant ?? tenantId);

    [Fact]
    public async Task OneBatch_InsertsUpdatesAndDeletes_AndDeleteWinsOverAnUpsert()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();

        await store.ApplyChangesAsync(
            new Dictionary<string, Doc> { ["kept"] = new(1), ["updated"] = new(1), ["deleted"] = new(1), ["both"] = new(1) },
            [], ct);

        await store.ApplyChangesAsync(
            new Dictionary<string, Doc> { ["updated"] = new(2), ["inserted"] = new(3), ["both"] = new(4), ["new-and-gone"] = new(5) },
            ["deleted", "both", "new-and-gone", "never-existed"],
            ct);

        var loaded = await store.LoadManyAsync(
            ["kept", "updated", "deleted", "both", "inserted", "new-and-gone", "never-existed"], ct);

        loaded.Should().BeEquivalentTo(new Dictionary<string, Doc>
        {
            ["kept"] = new(1),
            ["updated"] = new(2),
            ["inserted"] = new(3),
        });
    }

    [Fact]
    public async Task ListRecent_ReturnsTheVersionsRowsNewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();

        await store.ApplyChangesAsync(
            Enumerable.Range(0, 30).ToDictionary(i => $"doc-{i}", i => new Doc(i)), [], ct);
        // Noise the query must not see: another projection type, and another tenant.
        await Store(type: $"{_type}-other").ApplyChangesAsync(new Dictionary<string, Doc> { ["x"] = new(-1) }, [], ct);
        if (tenantId is not null)
            await Store(tenant: $"{tenantId}-other").ApplyChangesAsync(new Dictionary<string, Doc> { ["y"] = new(-2) }, [], ct);

        // One batch shares one now(); spread the timestamps so the order is defined.
        await ExecuteAsync(
            "UPDATE alberto_projection_states SET updated_at = now() - (substr(document_id, 5)::int * interval '1 second') " +
            "WHERE projection_type = @type AND document_id LIKE 'doc-%'", ct);

        var recent = await store.ListRecentAsync(10, ct);

        recent.Select(d => d.Value).Should().Equal(Enumerable.Range(0, 10));
    }

    [Fact]
    public async Task ListRecent_IsServedByTheRecentIndex_WithoutASort()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().ApplyChangesAsync(
            Enumerable.Range(0, 50).ToDictionary(i => $"doc-{i}", i => new Doc(i)), [], ct);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var off = new NpgsqlCommand("SET LOCAL enable_seqscan = off", conn, tx))
            await off.ExecuteNonQueryAsync(ct);

        var tenantFilter = tenantId is null ? "" : "tenant_id = @tenant AND ";
        await using var explain = new NpgsqlCommand(
            $"""
            EXPLAIN SELECT state FROM alberto_projection_states
            WHERE {tenantFilter}projection_type = @type AND rebuild_version = 1
            ORDER BY updated_at DESC LIMIT 20
            """, conn, tx);
        explain.Parameters.AddWithValue("type", _type);
        if (tenantId is not null)
            explain.Parameters.AddWithValue("tenant", tenantId);

        var plan = new StringBuilder();
        await using (var reader = await explain.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                plan.AppendLine(reader.GetString(0));

        plan.ToString().Should().Contain("idx_alberto_projection_states_recent")
            .And.NotContain("Sort", "the index already yields updated_at DESC within the version");
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("type", _type);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

[Trait("Category", "Integration")]
public sealed class SingleTenantPostgresStateStoreBatchTests(SingleTenantPostgresFixture fixture)
    : PostgresStateStoreBatchTests(fixture.DataSource, tenantId: null), IClassFixture<SingleTenantPostgresFixture>;

[Trait("Category", "Integration")]
public sealed class MultiTenantPostgresStateStoreBatchTests(MultiTenantDbFixture fixture)
    : PostgresStateStoreBatchTests(fixture.DataSource, tenantId: $"tenant-{Guid.NewGuid():N}"), IClassFixture<MultiTenantDbFixture>;
