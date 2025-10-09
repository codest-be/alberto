using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Alberto.Example.ComponentTests;

public class ServiceFixture : WebApplicationFactory<Program>, IServiceFixture, IAsyncLifetime
{
    public ServiceFixture()
    {
    }

    public HttpClient HttpClient { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        // Trigger creation of the host on the xUnit lifecycle event
        _ = Services;

        // Create HttpClient with TestServer handler
        HttpClient = CreateClient();

        return default;
    }

    public new ValueTask DisposeAsync()
    {
        HttpClient?.Dispose();
        return base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        // Add configuration FIRST, before services are registered
        builder.UseSetting("ConnectionStrings:alberto-db", "Host=localhost;Database=test;Username=test;Password=test");

        builder.ConfigureTestServices(services =>
        {
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