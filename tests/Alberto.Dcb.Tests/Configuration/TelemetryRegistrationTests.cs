using System.Diagnostics;
using System.Reflection;
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
using OpenTelemetry.Metrics;
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

    // ── Finding 1 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TelemetryEnabled_false_suppresses_Alberto_append_activities()
    {
        var exported = new List<Activity>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Telemetry:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module.WithInMemory().WithTelemetry());
        services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddSource(AlbertoMetrics.Name)
            .AddInMemoryExporter(exported));

        await using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();
        provider.GetRequiredService<IEnumerable<IHostedService>>();

        var eventStore = provider.GetRequiredKeyedService<IEventStore>("orders");
        await eventStore.AppendAsync(
            [new EventToPersist
            {
                EventType = new EventType("test-event"),
                Tags = [],
                EventData = "{}",
            }],
            cancellationToken: TestContext.Current.CancellationToken);

        tracerProvider.ForceFlush();

        exported.Should().NotContain(a => a.OperationName == AlbertoMetrics.AppendActivityName,
            "Telemetry:Enabled = false must suppress Alberto append activities");
    }

    [Fact]
    public async Task TelemetryEnabled_true_produces_Alberto_append_activity()
    {
        var exported = new List<Activity>();

        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory().WithTelemetry());
        services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddSource(AlbertoMetrics.Name)
            .AddInMemoryExporter(exported));

        await using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();
        provider.GetRequiredService<IEnumerable<IHostedService>>();

        var eventStore = provider.GetRequiredKeyedService<IEventStore>("orders");
        await eventStore.AppendAsync(
            [new EventToPersist
            {
                EventType = new EventType("test-event"),
                Tags = [],
                EventData = "{}",
            }],
            cancellationToken: TestContext.Current.CancellationToken);

        tracerProvider.ForceFlush();

        exported.Should().ContainSingle(a => a.OperationName == AlbertoMetrics.AppendActivityName,
            "Telemetry:Enabled = true (default) must produce exactly one Alberto append activity");
    }

    // ── Finding 2 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTelemetry_called_by_two_modules_produces_exactly_one_append_activity()
    {
        // Proves that calling AddSource(AlbertoMetrics.Name) from two modules does not
        // duplicate telemetry: one append on one module → exactly one Alberto.Append activity.
        var exported = new List<Activity>();

        var services = new ServiceCollection();
        services.AddAlberto("mod-a", module => module.WithInMemory().WithTelemetry());
        services.AddAlberto("mod-b", module => module.WithInMemory().WithTelemetry());
        services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddSource(AlbertoMetrics.Name)
            .AddInMemoryExporter(exported));

        await using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();
        provider.GetRequiredService<IEnumerable<IHostedService>>();

        var eventStore = provider.GetRequiredKeyedService<IEventStore>("mod-a");
        await eventStore.AppendAsync(
            [new EventToPersist
            {
                EventType = new EventType("test-event"),
                Tags = [],
                EventData = "{}",
            }],
            cancellationToken: TestContext.Current.CancellationToken);

        tracerProvider.ForceFlush();

        exported.Count(a => a.OperationName == AlbertoMetrics.AppendActivityName)
            .Should().Be(1, "one append on one module must produce exactly one Alberto.Append activity, not one per WithTelemetry() call");
    }

    // ── Finding I3 ─────────────────────────────────────────────────────────

    [Fact]
    public void AddAlbertoInstrumentation_TracerProviderBuilder_is_not_obsolete()
    {
        // The spec retains AddAlbertoInstrumentation for manual TracerProvider wiring.
        // It must not carry [Obsolete] — the attribute was added by mistake.
        typeof(Alberto.Dcb.Telemetry.ServiceCollectionExtensions)
            .GetMethods()
            .Where(m => m.Name == "AddAlbertoInstrumentation"
                     && m.GetParameters().Length == 1
                     && m.GetParameters()[0].ParameterType == typeof(TracerProviderBuilder))
            .Should().ContainSingle()
            .Which.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false)
            .Should().BeEmpty("AddAlbertoInstrumentation must not be marked [Obsolete]");
    }

    [Fact]
    public void AddAlbertoInstrumentation_MeterProviderBuilder_is_not_obsolete()
    {
        typeof(Alberto.Dcb.Telemetry.ServiceCollectionExtensions)
            .GetMethods()
            .Where(m => m.Name == "AddAlbertoInstrumentation"
                     && m.GetParameters().Length == 1
                     && m.GetParameters()[0].ParameterType == typeof(OpenTelemetry.Metrics.MeterProviderBuilder))
            .Should().ContainSingle()
            .Which.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false)
            .Should().BeEmpty("AddAlbertoInstrumentation must not be marked [Obsolete]");
    }

    [Fact]
    public async Task AddAlbertoInstrumentation_is_idempotent_with_WithTelemetry()
    {
        // AddAlbertoInstrumentation() is retained for manual TracerProvider wiring.
        // When called alongside WithTelemetry(), it must not double-register the source.
        var exported = new List<Activity>();

        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory().WithTelemetry());
#pragma warning disable CS0618
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddAlbertoInstrumentation()    // should become non-obsolete after fix
                .AddInMemoryExporter(exported));
#pragma warning restore CS0618

        await using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();
        provider.GetRequiredService<IEnumerable<IHostedService>>();

        using var activity = AlbertoMetrics.Source.StartActivity("i3-idempotency-span");
        activity?.Stop();

        tracerProvider.ForceFlush();

        exported.Count(a => a.OperationName == "i3-idempotency-span")
            .Should().Be(1,
                "AddAlbertoInstrumentation() plus WithTelemetry() must not double-register the source");
    }

    [Fact]
    public async Task Pre_existing_OpenTelemetry_setup_survives_AddAlberto()
    {
        // Proves Alberto does not clobber an app's own AddOpenTelemetry() setup
        // that was registered before AddAlberto().
        var exported = new List<Activity>();
        using var appSource = new ActivitySource("MyApp");

        var services = new ServiceCollection();

        // App configures OTel first, before AddAlberto.
        services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddSource("MyApp")
            .AddInMemoryExporter(exported));

        services.AddAlberto("orders", module => module.WithInMemory().WithTelemetry());

        await using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetRequiredService<TracerProvider>();
        provider.GetRequiredService<IEnumerable<IHostedService>>();

        // App's own activity
        using var appActivity = appSource.StartActivity("my-app-span");
        appActivity?.Stop();

        // Alberto append activity
        var eventStore = provider.GetRequiredKeyedService<IEventStore>("orders");
        await eventStore.AppendAsync(
            [new EventToPersist
            {
                EventType = new EventType("test-event"),
                Tags = [],
                EventData = "{}",
            }],
            cancellationToken: TestContext.Current.CancellationToken);

        tracerProvider.ForceFlush();

        exported.Should().ContainSingle(a => a.OperationName == "my-app-span",
            "the app's own OTel source must still be collected after AddAlberto");
        exported.Should().ContainSingle(a => a.OperationName == AlbertoMetrics.AppendActivityName,
            "Alberto's append activity must also be collected");
    }
}
