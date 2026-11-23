using Alberto.EntityFramework.Inline;
using Alberto.Postgres;
using Alberto.Projections;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Alberto.Tests.EntityFramework.EfInlineProjectionOrderingTests;

namespace Alberto.Tests.EntityFramework;

[Trait("Category", "Integration")]
public sealed class TransactionalProjectionSwallowedErrorTests(EfEventStoreFixture fixture) : IClassFixture<EfEventStoreFixture>
{
    private sealed class SwallowingProjection : ITransactionalInlineProjection
    {
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { "counter-incremented" };
        public Task ProcessAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default) => Task.CompletedTask;
        public async Task ProcessInTransactionAsync(IReadOnlyList<IEventEnvelope> events, AppendTransaction tx, CancellationToken ct = default)
        {
            try
            {
                await using var cmd = new NpgsqlCommand("select 1/0", (NpgsqlConnection)tx.Connection, (NpgsqlTransaction)tx.Transaction);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException) { }
        }
    }

    [Fact]
    public async Task ProjectionSwallowingADatabaseError_AppendMustNotReportSuccessWhenRolledBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new EventStore(new PostgresEventStoreBackend(fixture.DataSource), [new SwallowingProjection()]);
        var id = Guid.NewGuid().ToString();
        var marker = $"swallow-{Guid.NewGuid():N}";
        var ev = Increment(id, 1) with { Tags = [new EventTag("probe", marker)] };
        Exception? thrown = null;
        IReadOnlyCollection<IEventEnvelope>? result = null;
        try { result = await store.AppendAsync([ev], cancellationToken: ct); } catch (Exception e) { thrown = e; }
        var read = await store.ReadAsync(DcbQuery.ByTags(new EventTag("probe", marker).Value), 0, 100, ct);
        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Null(result);
        Assert.Empty(read);
    }
}
