using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

// Handler and event types live at namespace level so ProcessorId.For<T>() derives
// the simple type name without a declaring-class prefix.
namespace Alberto.Dcb.Tests.Configuration;

[EventType("shipment-dispatched")]
internal sealed record ShipmentDispatched(string Id) : IEvent;

internal sealed class ShipmentSummary { }

internal sealed class ShipmentNotifier
{
    public Task HandleAsync(ShipmentDispatched e, CancellationToken ct) => Task.CompletedTask;
}

[ProcessorId("shipments.legacy")]
internal sealed class RenamedShipmentNotifier
{
    public Task HandleAsync(ShipmentDispatched e, CancellationToken ct) => Task.CompletedTask;
}

public class ProcessorRegistrationTests
{
    private static AlbertoModuleDefinition Resolve(IServiceCollection services) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("orders");

    private static IServiceCollection Module(Action<DcbModuleBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ShipmentNotifier>();
        services.AddSingleton<RenamedShipmentNotifier>();
        services.AddAlberto("orders", module =>
        {
            module.WithInMemory();
            configure(module);
        });
        return services;
    }

    [Fact]
    public void A_handler_based_reactor_derives_its_processor_id()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched, ShipmentNotifier>(h => h.HandleAsync));

        Resolve(services).Processors.Should().ContainSingle()
            .Which.ProcessorId.Should().Be("ShipmentNotifier");
    }

    [Fact]
    public void The_ProcessorId_attribute_overrides_the_derived_id()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched, RenamedShipmentNotifier>(h => h.HandleAsync));

        Resolve(services).Processors.Should().ContainSingle()
            .Which.ProcessorId.Should().Be("shipments.legacy");
    }

    [Fact]
    public void An_explicit_processor_id_still_wins()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched, ShipmentNotifier>(
            h => h.HandleAsync, processorId: "explicit"));

        Resolve(services).Processors.Should().ContainSingle()
            .Which.ProcessorId.Should().Be("explicit");
    }

    [Fact]
    public void A_declared_processor_records_its_handler_type()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched, ShipmentNotifier>(h => h.HandleAsync));

        Resolve(services).Processors[0].HandlerType.Should().Be<ShipmentNotifier>();
    }

    [Fact]
    public void Execution_options_are_configured_with_a_with_expression()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched, ShipmentNotifier>(
            h => h.HandleAsync,
            configure: o => o with { BatchingMode = ProcessorBatchingMode.IfSupported, MaxConcurrency = 4 }));

        var execution = Resolve(services).Processors[0].Execution;

        execution.BatchingMode.Should().Be(ProcessorBatchingMode.IfSupported);
        execution.MaxConcurrency.Should().Be(4);
    }

    [Fact]
    public void Two_reactors_on_the_same_handler_type_are_reported_as_a_duplicate_id()
    {
        // Capture the definition before DI validation runs, because IOptionsMonitor.Get()
        // triggers IValidateOptions<T> and an invalid module throws OptionsValidationException.
        AlbertoModuleDefinition? captured = null;
        var services = new ServiceCollection();
        services.AddSingleton<ShipmentNotifier>();
        services.AddAlberto("orders", module =>
        {
            module.WithInMemory();
            module.ReactTo<ShipmentDispatched, ShipmentNotifier>(h => h.HandleAsync);
            module.ReactTo<ShipmentDispatched, ShipmentNotifier>(h => h.HandleAsync);
            captured = module.Definition;
        });

        var failures = new AlbertoModuleValidator().Collect(captured!);

        failures.Should().Contain(f => f.Code == "ALB0002");
        failures.Single(f => f.Code == "ALB0002").Problem.Should().Contain("ShipmentNotifier");
    }

    [Fact]
    public void A_lambda_reactor_still_requires_an_explicit_processor_id()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched>(
            _ => (_, _) => Task.CompletedTask,
            processorId: "shipment-lambda"));

        Resolve(services).Processors.Should().ContainSingle()
            .Which.HandlerType.Should().BeNull();
    }

    [Fact]
    public void AddProjection_declares_a_projection_processor()
    {
        var declaration = DeclareProjection.For<ShipmentSummary>("shipment-summary")
            .On<ShipmentDispatched>(id: e => e.Id, apply: (state, _, _) => state)
            .Build();

        var services = Module(m => m.AddProjection<ShipmentSummary>(
            declaration,
            stateStoreFactory: _ => throw new InvalidOperationException("not needed by this test")));

        var processor = Resolve(services).Processors.Should().ContainSingle()
            .Which;
        processor.ProcessorId.Should().Be("shipment-summary");
        processor.Kind.Should().Be(ProcessorKind.Projection);
    }
}
