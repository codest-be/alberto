using System.Diagnostics;
using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Telemetry;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Alberto.Dcb.Tests.Configuration;

public class TelemetryRegistrationTests
{
    private static AlbertoModuleDefinition Resolve(IServiceCollection services) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("orders");

    [Fact]
    public void WithTelemetry_marks_the_module_as_instrumented()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory().WithTelemetry());

        var definition = Resolve(services);

        definition.TelemetryEnabled.Should().BeTrue();
        definition.Telemetry.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Telemetry_can_be_switched_off_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Telemetry:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module.WithInMemory().WithTelemetry());

        Resolve(services).Telemetry.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Alberto_activities_are_collected_without_calling_AddAlbertoInstrumentation()
    {
        var exported = new List<Activity>();

        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory().WithTelemetry());
        services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(exported));

        await using var provider = services.BuildServiceProvider();
        // Resolve TracerProvider first to register ActivityListeners before starting activities.
        // GetRequiredService<IEnumerable<IHostedService>>() also ensures Alberto's hosted services
        // are resolved as singletons, consistent with a real hosted application.
        var tracerProvider = provider.GetRequiredService<TracerProvider>();
        provider.GetRequiredService<IEnumerable<IHostedService>>();

        using var activity = AlbertoMetrics.Source.StartActivity("test-span");
        activity?.Stop();

        tracerProvider.ForceFlush();

        exported.Should().ContainSingle(a => a.OperationName == "test-span");
    }
}
