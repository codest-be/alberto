using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Pins the resolved default of <see cref="OrphanCheckpointPolicy"/>: <c>Warn</c>, in every
/// environment. A deleted processor leaves an inert row, and taking a production host down over
/// it costs more than the renames the check exists to catch. <c>Strict</c> is reachable through
/// configuration only, and nothing in the host may raise the policy on its own.
/// </summary>
public class OrphanPolicyDefaultTests
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

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Nothing_configured_stays_Warn_in_every_environment(string environmentName)
    {
        var definition = Resolve(environmentName);

        definition.Checkpoints.OrphanPolicy.Should().Be(OrphanCheckpointPolicy.Warn,
            "an orphaned checkpoint must never be able to fail a deployment that did not ask for it");
    }

    [Fact]
    public void Explicit_Strict_in_configuration_is_honoured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Checkpoints:OrphanPolicy"] = "Strict",
            })
            .Build();

        var definition = Resolve("Production", configuration);

        definition.Checkpoints.OrphanPolicy.Should().Be(OrphanCheckpointPolicy.Strict);
    }

    [Fact]
    public void Explicit_Off_in_configuration_is_honoured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Checkpoints:OrphanPolicy"] = "Off",
            })
            .Build();

        var definition = Resolve("Development", configuration);

        definition.Checkpoints.OrphanPolicy.Should().Be(OrphanCheckpointPolicy.Off);
    }
}
