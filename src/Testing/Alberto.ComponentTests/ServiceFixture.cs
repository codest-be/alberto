using Alberto.EventStore.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.v3;

namespace Alberto.ComponentTests;

public class ServiceFixture<TProgram> : WebApplicationFactory<TProgram>, IServiceFixture, IAsyncLifetime where TProgram : class
{
    private readonly SubscriptionEventCollector _collector = new();

    public ServiceFixture()
    {
        EventStoreBackend = new InMemoryEventStoreBackend(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<InMemoryEventStoreBackend>());
    }

    /// <summary>
    /// Gets the subscription event collector for waiting on processed events in tests.
    /// </summary>
    protected SubscriptionMetadataRegistry SubscriptionMetadataRegistry { get; } = new();

    protected InMemoryEventStoreBackend EventStoreBackend { get; }
    protected ITestOutputHelper TestOutputHelper { get; init; } = new TestOutputHelper();

    public ValueTask InitializeAsync()
    {
        _ = Services;

        _collector.SetMetadataRegistry(SubscriptionMetadataRegistry);

        return default;
    }

    public new ValueTask DisposeAsync()
    {
        return base.DisposeAsync();
    }

    public UseCase UseCase()
    {
        return new UseCase(new ScenarioContext(TestOutputHelper, this));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        // Add configuration FIRST, before services are registered
        builder.UseSetting("ConnectionStrings:alberto-db", "Host=localhost;Database=test;Username=test;Password=test");

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(_collector);

            // Remove all hosted services (subscription polling, etc.) for testing
            var hostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();

            foreach (var service in hostedServices)
            {
                services.Remove(service);
            }

            ConfigureTestServices(services);
        });
    }

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }
}