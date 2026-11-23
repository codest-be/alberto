using System.Data.Common;
using Alberto.EntityFramework;
using Alberto.InMemory;
using Alberto.Projections;
using Alberto.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests.EntityFramework;

/// <summary>
/// An EF projection batch reads its documents once: the tracked rows it folds onto are the
/// rows it writes, rather than an untracked load followed by a tracked re-read.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfStateStoreSingleReadTests(EfProjectionTestFixture fixture)
    : IClassFixture<EfProjectionTestFixture>
{
    private const string ModuleKey = "single_read";

    [Fact]
    public async Task A_batch_issues_one_read_and_writes_inserts_and_updates()
    {
        var (processor, reads) = Build();
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        var c = Guid.NewGuid().ToString();

        await ProcessAsync(processor,
            EfTestEnvelopes.ForCounterIncremented(a, 1, position: 1),
            EfTestEnvelopes.ForCounterIncremented(b, 1, position: 2));

        reads.Count = 0;
        await ProcessAsync(processor,
            EfTestEnvelopes.ForCounterIncremented(a, 2, position: 3),
            EfTestEnvelopes.ForCounterIncremented(b, 3, position: 4),
            EfTestEnvelopes.ForCounterIncremented(c, 4, position: 5),
            EfTestEnvelopes.ForCounterIncremented(a, 5, position: 6));

        // the batch's documents are read once and written through the same context
        Assert.Equal(1, reads.Count);
        Assert.Equal(8, (await fixture.ReadEntityAsync(a))!.Counter);
        Assert.Equal(4, (await fixture.ReadEntityAsync(b))!.Counter);
        Assert.Equal(4, (await fixture.ReadEntityAsync(c))!.Counter);
    }

    [Fact]
    public async Task An_in_place_change_the_handler_did_not_write_is_not_persisted()
    {
        var (processor, _) = Build();
        var written = Guid.NewGuid().ToString();
        var untouched = Guid.NewGuid().ToString();

        await ProcessAsync(processor,
            EfTestEnvelopes.ForCounterIncremented(written, 1, position: 1),
            EfTestEnvelopes.ForCounterIncremented(untouched, 1, position: 2));

        // Amount 0 mutates the loaded entity and answers Unchanged.
        await ProcessAsync(processor,
            EfTestEnvelopes.ForCounterIncremented(written, 1, position: 3),
            EfTestEnvelopes.ForCounterIncremented(untouched, 0, position: 4));

        Assert.Equal(2, (await fixture.ReadEntityAsync(written))!.Counter);
        // a document the handler left Unchanged is not written, whatever it did to the instance
        Assert.Equal(1, (await fixture.ReadEntityAsync(untouched))!.Counter);
    }

    private (IBatchableProcessor Processor, ReadCounter Reads) Build()
    {
        var reads = new ReadCounter();
        var processorId = $"single_read_{Guid.NewGuid():N}";

        var declaration = DeclareProjection.For<CounterEntity>(processorId)
            .On<CounterIncremented>(
                id: e => e.EntityId,
                apply: (state, e, _) =>
                {
                    if (e.Amount == 0)
                    {
                        state.Counter = -999;
                        return ProjectionResults.Unchanged<CounterEntity>();
                    }

                    state.Counter += e.Amount;
                    return ProjectionResults.Set(state);
                })
            .Build();

        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, m =>
        {
            m.WithInMemory();
            m.WithEntityFramework<EfTestDbContext>(o => o.UseNpgsql(fixture.ConnectionString).AddInterceptors(reads));
            m.AddEfProjection<CounterEntity, EfTestDbContext>(declaration);
        });

        var processor = services.BuildServiceProvider().GetRequiredKeyedService<IEventProcessor>(ModuleKey);
        return ((IBatchableProcessor)processor, reads);
    }

    private static Task ProcessAsync(IBatchableProcessor processor, params IEventEnvelope[] envelopes) =>
        processor.ProcessBatchAsync(envelopes, TestContext.Current.CancellationToken);

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
