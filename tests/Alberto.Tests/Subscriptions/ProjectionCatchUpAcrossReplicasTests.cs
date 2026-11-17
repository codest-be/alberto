using System.Text.Json;
using Alberto.InMemory;
using Alberto.Postgres;
using Alberto.Projections;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Read-your-writes when leases spread processors over replicas: the replica that took the
/// command is often not the one running the projection, and its wait must still return once the
/// owner has written its checkpoint.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProjectionCatchUpAcrossReplicasTests(SingleTenantPostgresFixture fixture)
    : IClassFixture<SingleTenantPostgresFixture>
{
    private const string ModuleKey = "catchup_replicas";

    // One processor per test: a lease or checkpoint one test leaves behind must not decide another.
    private readonly string ProcessorId = $"catchup-replicas-{Guid.NewGuid():N}";

    [Fact]
    public async Task WaitForProjection_OnTheReplicaWithoutTheLease_ReturnsOnceTheOwnerSavesItsCheckpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The owner starts first: lease acquisition happens inside StartAsync, so it takes the
        // processor and the second replica finds it held.
        await using var owner = await StartReplicaAsync("replica-owner", cancellationToken);
        await using var other = await StartReplicaAsync("replica-other", cancellationToken);

        (await LeaseHolderAsync(cancellationToken)).Should().Be("replica-owner");

        await AppendSeatAsync(other, cancellationToken);

        // No flush interval is configured: the 30 s timer is not what gets the checkpoint to the
        // database, the owner's write after the batch is.
        var catchUp = other.Services.GetRequiredKeyedService<ProjectionCatchUp>(ModuleKey);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await catchUp.WaitForProjectionAsync(ProcessorId, TimeSpan.FromSeconds(1), cancellationToken);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));

        (await LeaseHolderAsync(cancellationToken)).Should().Be("replica-owner",
            because: "the wait returned on a replica that never ran the projection");
    }

    [Fact]
    public async Task Owner_ThatLostItsLease_DoesNotWriteItsCheckpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var owner = await StartReplicaAsync("replica-fenced", cancellationToken);
        (await LeaseHolderAsync(cancellationToken)).Should().Be("replica-fenced");

        // Control: while the lease is held, the batch's checkpoint reaches the database.
        // The owner's own wait is answered by its cache, which moves before the write lands, so
        // the control waits on the database itself.
        var first = await AppendSeatAsync(owner, cancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while ((await StoredCheckpointAsync(cancellationToken) ?? 0) < first && DateTime.UtcNow < deadline)
            await Task.Delay(20, cancellationToken);
        (await StoredCheckpointAsync(cancellationToken)).Should().BeGreaterThanOrEqualTo(first);

        // Another replica takes the lease over, in a later generation. The owner has not
        // noticed yet: its next renewal is seconds away, and it keeps processing until then.
        await using (var steal = fixture.DataSource.CreateCommand(
            "UPDATE alberto_processor_leases SET replica_id = 'replica-intruder', fence_token = fence_token + 1, " +
            "expires_at = now() + interval '1 hour' WHERE consumer_id = @consumer AND processor_id = @processor"))
        {
            steal.Parameters.AddWithValue("consumer", ModuleKey);
            steal.Parameters.AddWithValue("processor", ProcessorId);
            (await steal.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        var second = await AppendSeatAsync(owner, cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

        (await StoredCheckpointAsync(cancellationToken)).Should().BeLessThan(second,
            because: "the fenced write of a replica without the lease is rejected");
    }

    private static async Task<long> AppendSeatAsync(Replica replica, CancellationToken cancellationToken)
    {
        var appended = await replica.Services.GetRequiredKeyedService<IEventStore>(ModuleKey).AppendAsync(
            [
                new EventToPersist
                {
                    EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(SeatReservedForCatchUp))),
                    Tags = [new EventTag("show", "show-1")],
                    EventData = JsonSerializer.Serialize(new SeatReservedForCatchUp("show-1")),
                },
            ],
            cancellationToken: cancellationToken);
        return appended.Max(e => e.GlobalPosition);
    }

    private async Task<long?> StoredCheckpointAsync(CancellationToken cancellationToken)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT last_position FROM alberto_processor_checkpoints WHERE processor_id = @processor");
        command.Parameters.AddWithValue("processor", ProcessorId);
        return (long?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<Replica> StartReplicaAsync(string replicaId, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, builder => builder
            .WithPostgres(o => o with
            {
                ConnectionString = fixture.ConnectionString,
                AutoMigrate = false,
                EnableNotifyListener = false,
            })
            .AddProjection(
                DeclareProjection.For<CatchUpOccupancy>(ProcessorId)
                    .On<SeatReservedForCatchUp>(
                        e => e.ShowId,
                        (state, _, _) => state with { SeatsTaken = state.SeatsTaken + 1 })
                    .Build(),
                _ => _ => new InMemoryStateStore<CatchUpOccupancy>())
            .WithControlLoop(o => o with
            {
                PollingInterval = TimeSpan.FromMilliseconds(20),
                Leases = o.Leases with { Enabled = true, ReplicaId = replicaId },
            }));

        var replica = new Replica(services.BuildServiceProvider());
        foreach (var hosted in replica.Hosted)
            await hosted.StartAsync(cancellationToken);
        return replica;
    }

    private async Task<string?> LeaseHolderAsync(CancellationToken cancellationToken)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT replica_id FROM alberto_processor_leases WHERE consumer_id = @consumer AND processor_id = @processor");
        command.Parameters.AddWithValue("consumer", ModuleKey);
        command.Parameters.AddWithValue("processor", ProcessorId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private sealed class Replica(ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public IReadOnlyList<IHostedService> Hosted { get; } = services.GetServices<IHostedService>().ToList();

        public async ValueTask DisposeAsync()
        {
            foreach (var hosted in Hosted.Reverse())
                await hosted.StopAsync(CancellationToken.None);
            await Services.DisposeAsync();
        }
    }
}
