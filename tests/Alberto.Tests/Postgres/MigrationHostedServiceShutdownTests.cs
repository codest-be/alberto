using Alberto.Configuration;
using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// A host can be stopped more than once, and the second stop can come after the host was disposed
/// (a <c>WebApplicationFactory</c> does exactly that). Stopping the migration service then must
/// be a no-op, as it is for every other hosted service (#181).
/// </summary>
public sealed class MigrationHostedServiceShutdownTests
{
    private const string ConnectionString = "Host=localhost;Database=alberto;Username=x;Password=y";

    private static AlbertoMigrationHostedService NewService()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = ConnectionString }));

        var definitions = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>();

        return new AlbertoMigrationHostedService("orders", definitions);
    }

    [Fact]
    public async Task StopAsync_after_Dispose_completes_without_throwing()
    {
        var service = NewService();
        service.Dispose();

        var stop = () => service.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopAsync_twice_and_Dispose_twice_are_both_harmless()
    {
        var service = NewService();

        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        service.Dispose();
        service.Dispose();

        var stop = () => service.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }
}

/// <summary>
/// The same guarantee, through a real host against a real database, including the sharded case
/// where the service still has a retry loop running when the host goes away.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MigrationHostShutdownTests(PostgresCluster cluster)
{
    /// <summary>Refuses connections immediately rather than hanging until a timeout.</summary>
    private const string Unreachable =
        "Host=localhost;Port=1;Database=alberto;Username=x;Password=y;Timeout=2";

    [Fact]
    public async Task A_host_stopped_again_after_it_was_disposed_does_not_throw()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await cluster.CloneAsync(PostgresTemplates.Empty, "stoptwice", ct);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto("stop_twice", module => module
            .WithPostgres(o => o with
            {
                ConnectionString = database,
                Schema = "stop_twice",
                EnableNotifyListener = false,
                MaxPoolSize = 5,
            }));

        var host = builder.Build();
        await host.StartAsync(ct);
        await host.StopAsync(ct);
        host.Dispose();

        var stopAgain = () => host.StopAsync(ct);

        await stopAgain.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_sharded_host_disposed_while_a_shard_is_retrying_can_still_be_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var db1 = await cluster.CloneAsync(PostgresTemplates.MultiTenant, "stoptwicedb1", ct);
        var catalog = await cluster.CloneAsync(PostgresTemplates.Empty, "stoptwicecatalog", ct);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto("stop_twice_sharded", module => module
            .WithPostgres(o => o with { EnableNotifyListener = false, MaxPoolSize = 5 })
            .WithTenancy(t => t.AcrossPostgresDatabases(s => s
                .WithCatalog(o => o with { ConnectionString = catalog })
                .AddShard("db1", o => o with { ConnectionString = db1 })
                .AddShard("db2", o => o with { ConnectionString = Unreachable }))));

        var host = builder.Build();

        // db2 is down, so its migration service is left with a retry loop running. Disposing
        // without a stop first is the path where that loop used to be orphaned.
        await host.StartAsync(ct);
        host.Dispose();

        var stop = () => host.StopAsync(ct);

        await stop.Should().NotThrowAsync();
        await stop.Should().NotThrowAsync();
    }
}
