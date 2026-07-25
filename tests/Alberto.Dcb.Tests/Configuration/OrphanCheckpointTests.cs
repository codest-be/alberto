using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

public class OrphanCheckpointTests
{
    private sealed class FakeInventory(params string[] processorIds) : ICheckpointInventory
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(processorIds);
        }
    }

    private static AlbertoModuleDefinition Definition(
        OrphanCheckpointPolicy policy,
        params string[] declaredProcessorIds) => new()
    {
        ModuleKey = "orders",
        Checkpoints = new CheckpointOptions { OrphanPolicy = policy },
        Processors =
        [
            .. declaredProcessorIds.Select(id => new ProcessorDeclaration
            {
                ProcessorId = id,
                Kind = ProcessorKind.Reactor,
            }),
        ],
    };

    private static Task RunAsync(
        AlbertoModuleDefinition definition,
        ICheckpointInventory? inventory) =>
        new OrphanCheckpointHostedService(
            definition,
            inventory,
            NullLogger<OrphanCheckpointHostedService>.Instance)
            .StartAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Strict_fails_startup_when_a_checkpoint_has_no_processor()
    {
        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"),
            new FakeInventory("OrderSummary", "OldReactorName"));

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("OldReactorName");
        exception.Which.Message.Should().Contain("ops checkpoint rename");
    }

    [Fact]
    public async Task Strict_is_silent_when_every_checkpoint_is_claimed()
    {
        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"),
            new FakeInventory("OrderSummary"));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Warn_does_not_fail_startup()
    {
        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Warn, "OrderSummary"),
            new FakeInventory("OldReactorName"));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Off_does_not_read_the_inventory()
    {
        var inventory = new FakeInventory("OldReactorName");

        var act = () => RunAsync(Definition(OrphanCheckpointPolicy.Off, "OrderSummary"), inventory);

        await act.Should().NotThrowAsync();
        inventory.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_store_that_cannot_enumerate_is_skipped_rather_than_failing()
    {
        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"),
            inventory: null);

        await act.Should().NotThrowAsync();
    }
}
