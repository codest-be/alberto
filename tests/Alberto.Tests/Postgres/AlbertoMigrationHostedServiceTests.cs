using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests.Postgres;

public class AlbertoMigrationHostedServiceTests
{
    [Fact]
    public async Task StopAsync_after_Dispose_is_a_noop()
    {
        // WebApplicationFactory can stop a host it already disposed; the second stop
        // must not throw ObjectDisposedException (#181).
        var service = CreateService();
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private static AlbertoMigrationHostedService CreateService()
    {
        // An in-memory backend keeps TryMigrate a no-op, so no database is needed.
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());
        var definitions = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>();

        return new AlbertoMigrationHostedService("orders", definitions);
    }
}
