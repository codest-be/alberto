using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Alberto.Tests.Subscriptions;

[Trait("Category", "Integration")]
[Collection(StableHeadBarrierCollection.Name)]
public sealed class ConcurrentTenantAppendDeliveryTests(MultiTenantDbFixture fixture)
    : IClassFixture<MultiTenantDbFixture>
{
    private const string ModuleKey = "tenant_delivery";
    private const int Tenants = 8;
    private const int AppendsPerTenant = 40;

    [Fact(Explicit = true)]
    public async Task Every_committed_event_of_concurrent_tenant_appends_reaches_a_caught_up_processor()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"delivery-{Guid.NewGuid():N}";
        var received = new ConcurrentDictionary<long, byte>();

        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, module => module
            .WithTenancy()
            .WithPostgres(o => o with
            {
                ConnectionString = fixture.ConnectionString,
                AutoMigrate = false,
                EnableNotifyListener = true,
            })
            .ReactTo<DeliveryProbe>(_ => (_, context, _) =>
            {
                received.TryAdd(context.Position, 0);
                return Task.CompletedTask;
            }, processorId)
            .WithControlLoop(o => o with
            {
                PollingInterval = TimeSpan.FromMilliseconds(10),
                HeadRefreshInterval = TimeSpan.FromMilliseconds(10),
            }));
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(ct);
        try
        {
            var store = new PostgresTenantEventStoreBackend(fixture.DataSource);
            var warmUp = await store.AppendForTenant("delivery-warm-up", [Probe()], cancellationToken: ct);
            await Gap.Until(() => received.ContainsKey(warmUp.Single().GlobalPosition), TimeSpan.FromSeconds(20), ct);

            var committed = new ConcurrentBag<long>();
            await Task.WhenAll(Enumerable.Range(0, Tenants).Select(t => Task.Run(async () =>
            {
                var random = new Random(t);
                for (var i = 0; i < AppendsPerTenant; i++)
                {
                    var events = Enumerable.Range(0, random.Next(1, 9)).Select(_ => Probe()).ToList();
                    var workInTransaction = random.Next(3) == 0 ? random.Next(1, 15) / 1000.0 : 0;
                    var appended = await store.AppendForTenant($"delivery-{t}", events, null, null,
                        workInTransaction == 0 ? null : (_, tx, token) => WorkAsync(tx, workInTransaction, token), ct);
                    foreach (var e in appended)
                        committed.Add(e.GlobalPosition);
                }
            }, ct)));

            var last = committed.Max();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            await Gap.Until(() => received.ContainsKey(last) || DateTime.UtcNow > deadline, TimeSpan.FromSeconds(30), ct);

            var missing = committed.Where(p => !received.ContainsKey(p)).Order().ToList();
            Assert.True(missing.Count == 0,
                $"{missing.Count} of {committed.Count} committed events never reached the processor:\n" +
                await DescribeAsync(missing, ct));
        }
        finally
        {
            foreach (var service in Enumerable.Reverse(hosted))
                await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WorkAsync(AppendTransaction tx, double seconds, CancellationToken ct)
    {
        await using var cmd = tx.Connection.CreateCommand();
        cmd.Transaction = tx.Transaction;
        cmd.CommandText = $"SELECT pg_sleep({seconds.ToString(CultureInfo.InvariantCulture)})";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string> DescribeAsync(IReadOnlyList<long> positions, CancellationToken ct)
    {
        await using var cmd = fixture.DataSource.CreateCommand(
            "SELECT global_position, tenant_id, pg_xact_id FROM alberto_events WHERE global_position = ANY(@p) ORDER BY global_position");
        cmd.Parameters.AddWithValue("p", positions.Take(50).ToArray());
        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            lines.Add($"global_position={reader.GetInt64(0)} tenant={reader.GetString(1)} pg_xact_id={reader.GetInt64(2)}");
        return string.Join('\n', lines);
    }

    private static IEventToPersist Probe() => new EventToPersist
    {
        EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(DeliveryProbe))),
        Tags = [],
        EventData = JsonSerializer.Serialize(new DeliveryProbe(Guid.NewGuid())),
    };

    [EventType("delivery-probe")]
    private sealed record DeliveryProbe(Guid Id) : IEvent;
}
