using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

/// <summary>
/// Pins the environment-dependent escalation of <see cref="OrphanCheckpointPolicy"/> that
/// runs inside the options configure callback in <see cref="ServiceCollectionExtensions.AddAlberto"/>.
/// Outside Development, an unset policy must escalate from Warn to Strict.
/// An operator who explicitly supplies Warn in configuration must not be escalated.
/// </summary>
public class ProductionEscalationTests
{
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "TestApp";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static AlbertoModuleDefinition Resolve(
        string environmentName,
        IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environmentName));
        services.AddSingleton<IConfiguration>(
            configuration ?? new ConfigurationBuilder().Build());
        services.AddAlberto("orders", m => m.WithInMemory());

        return services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("orders");
    }

    // ── Finding 1 ────────────────────────────────────────────────────────────

    [Fact]
    public void NonDevelopment_with_nothing_configured_escalates_to_Strict()
    {
        var definition = Resolve("Production");

        definition.Checkpoints.OrphanPolicy.Should().Be(OrphanCheckpointPolicy.Strict);
    }

    [Fact]
    public void Development_with_nothing_configured_stays_Warn()
    {
        var definition = Resolve("Development");

        definition.Checkpoints.OrphanPolicy.Should().Be(OrphanCheckpointPolicy.Warn);
    }

    // ── Finding 2 ────────────────────────────────────────────────────────────

    [Fact]
    public void NonDevelopment_with_explicit_Warn_in_configuration_stays_Warn()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Checkpoints:OrphanPolicy"] = "Warn",
            })
            .Build();

        var definition = Resolve("Production", configuration);

        definition.Checkpoints.OrphanPolicy.Should().Be(OrphanCheckpointPolicy.Warn,
            "an operator who explicitly sets Warn in appsettings.Production.json must not be silently escalated");
    }
}
