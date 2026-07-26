using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

[EventType("catchup-e2e-seat-reserved")]
public sealed record SeatReservedForCatchUp(string ShowId) : IEvent;

/// <summary>Seats taken per show — the projection the caller wants to read after writing.</summary>
public sealed record CatchUpOccupancy
{
    public int SeatsTaken { get; init; }
}

/// <summary>
/// The primitive against a real running control loop, wired the way an application is: append,
/// wait, read. No sleep anywhere in the test, which is the point — a sleep is a guess that is
/// either too short (flaky) or too long (slow), and this is what replaces it.
/// </summary>
public sealed class ProjectionCatchUpEndToEndTests
{
    private const string ModuleKey = "catchup_e2e";
    private const string ProcessorId = "catchup-e2e-occupancy";

    private static readonly ProjectionDeclaration<CatchUpOccupancy> Declaration =
        DeclareProjection.For<CatchUpOccupancy>(ProcessorId)
            .On<SeatReservedForCatchUp>(
                e => e.ShowId,
                (state, _, _) => state with { SeatsTaken = state.SeatsTaken + 1 })
            .Build();

    [Fact]
    public async Task WaitForProjection_LetsAReadImmediatelyAfterAWriteSeeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var occupancy = new InMemoryStateStore<CatchUpOccupancy>();

        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, builder => builder
            .WithInMemory()
            // No tenancy declared, so every event arrives with a null tenant and one store serves
            // the lot — the wait is about the loop's progress, not about which store it wrote to.
            .AddProjection(Declaration, _ => _ => occupancy)
            .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(20) }));

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(ct);

        try
        {
            var showId = Guid.NewGuid().ToString();
            var eventStore = provider.GetRequiredKeyedService<IEventStore>(ModuleKey);

            await AppendAsync(eventStore, showId, ct);
            await AppendAsync(eventStore, showId, ct);

            var catchUp = provider.GetRequiredKeyedService<ProjectionCatchUp>(ModuleKey);
            await catchUp.WaitForProjectionAsync(ProcessorId, cancellationToken: ct);

            var states = await occupancy.LoadManyAsync([showId], ct);
            states[showId].SeatsTaken.Should().Be(2,
                because: "the wait returns only once the loop has consumed everything appended before it");
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(CancellationToken.None);
        }
    }

    private static Task AppendAsync(IEventStore eventStore, string showId, CancellationToken ct) =>
        eventStore.AppendAsync(
            [
                new EventToPersist
                {
                    EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(SeatReservedForCatchUp))),
                    Tags = [new EventTag("show", showId)],
                    EventData = JsonSerializer.Serialize(new SeatReservedForCatchUp(showId)),
                }
            ],
            cancellationToken: ct);
}
