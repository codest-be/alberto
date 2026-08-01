using Alberto.EntityFramework;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests.EntityFramework;

/// <summary>
/// EF-5: the <c>afterCommit</c> callback fires on the live processor and deliberately does
/// <b>not</b> fire on a rebuild's shadow processor.
/// </summary>
/// <remarks>
/// <para>
/// The asymmetry is the point, and it is pinned here because it reads like an oversight at the
/// registration site. A shadow processor replays the whole log from position 0 into a version no
/// reader can see. <c>afterCommit</c> is a side effect on the outside world — notifications, cache
/// invalidation, downstream messages — so firing it per replayed event would re-emit every one of
/// those for the whole of history, once per rebuild, for state that is not live. A webhook that
/// has already fired cannot be unfired; a derived thing that was not refreshed can be refreshed
/// after promotion.
/// </para>
/// <para>
/// If someone later decides a rebuild should fire it, this test is what they have to change
/// deliberately, together with the documented contract on the overload.
/// </para>
/// </remarks>
public sealed class EfProjectionAfterCommitRebuildTests(EfProjectionTestFixture fixture)
    : IClassFixture<EfProjectionTestFixture>
{
    private const string ProcessorId = "after-commit-projection";
    private const string ModuleKey = "orders";

    [Fact]
    public async Task Live_processor_fires_afterCommit_for_the_events_it_handled()
    {
        var entityId = Guid.NewGuid().ToString();
        var (provider, seen) = BuildProvider();

        var live = provider.GetRequiredKeyedService<IEventProcessor>(ModuleKey);
        await ProcessAsync(live, EfTestEnvelopes.ForCounterIncremented(entityId, amount: 3, position: 1));

        seen.Should().ContainSingle("the live path is what afterCommit exists for")
            .Which.Should().ContainSingle().Which.GlobalPosition.Should().Be(1);
    }

    [Fact]
    public async Task Shadow_rebuild_processor_does_not_fire_afterCommit()
    {
        var entityId = Guid.NewGuid().ToString();
        var (provider, seen) = BuildProvider();

        var rebuildable = provider.GetRequiredKeyedService<RebuildableProjection>(ModuleKey);
        var shadow = rebuildable.CreateProcessor(() => 2);

        await ProcessAsync(shadow, EfTestEnvelopes.ForCounterIncremented(entityId, amount: 3, position: 1));

        // The write itself must still happen — this is a rebuild doing its job, minus the
        // side effect. Asserting only "no callback" would also pass if the processor did nothing.
        var versions = await fixture.ReadVersionsAsync(entityId);
        versions.Should().ContainKey(2).WhoseValue.Counter.Should().Be(3);

        seen.Should().BeEmpty(
            "a rebuild replays history into a version nothing reads yet; re-emitting every "
            + "notification the system has ever sent is not a rebuild, it is a second delivery");
    }

    /// <summary>
    /// Registers the module through the real <c>AddEfProjection</c> afterCommit overload — the
    /// point is to exercise the registration both processors are built by, not a hand-rolled
    /// processor — and returns the provider plus the list the callback appends to.
    /// </summary>
    private (ServiceProvider Provider, List<IReadOnlyList<IEventEnvelope>> Seen) BuildProvider()
    {
        var seen = new List<IReadOnlyList<IEventEnvelope>>();

        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, m =>
        {
            m.WithInMemory();
            m.WithEntityFramework<EfTestDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
            m.AddEfProjection<CounterEntity, EfTestDbContext>(
                EfProjectionTestFixture.BuildDeclaration(ProcessorId),
                afterCommit: _ => (events, _) =>
                {
                    lock (seen) seen.Add(events);
                    return Task.CompletedTask;
                });
        });

        return (services.BuildServiceProvider(), seen);
    }

    private static Task ProcessAsync(IEventProcessor processor, IEventEnvelope envelope) =>
        processor is IBatchableProcessor batchable
            ? batchable.ProcessBatchAsync([envelope], TestContext.Current.CancellationToken)
            : processor.ProcessEventAsync(envelope, TestContext.Current.CancellationToken);
}
