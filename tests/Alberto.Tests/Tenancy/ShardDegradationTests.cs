using Alberto.Postgres;
using Alberto.Tenancy;
using Alberto.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Alberto.Tests.Tenancy;

/// <summary>
/// One shard being down is not the module being down. A host whose second database refuses
/// connections still starts, still serves the tenants of the first, and says which one is missing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ShardDegradationTests(PostgresCluster cluster)
{
    private const string ModuleKey = "degraded_test";

    /// <summary>Refuses connections immediately rather than hanging until a timeout.</summary>
    private const string Unreachable =
        "Host=localhost;Port=1;Database=alberto;Username=x;Password=y;Timeout=2";

    private async Task<IHost> StartHostAsync(string db1, string catalog, bool withHealthChecks = false)
    {
        var builder = Host.CreateApplicationBuilder();

        // Alberto configures its check into HealthCheckServiceOptions unconditionally, but that is
        // inert until the app asks for a health pipeline of its own.
        if (withHealthChecks)
            builder.Services.AddHealthChecks();

        builder.Services.AddAlberto(ModuleKey, module => module
            .WithPostgres(o => o with { EnableNotifyListener = false, MaxPoolSize = 5 })
            .WithTenancy(t => t.AcrossPostgresDatabases(s => s
                .WithCatalog(o => o with { ConnectionString = catalog })
                .AddShard("db1", o => o with { ConnectionString = db1 })
                .AddShard("db2", o => o with { ConnectionString = Unreachable }))));

        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    [Fact]
    public async Task A_shard_that_is_unreachable_at_startup_degrades_the_module_instead_of_stopping_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var db1 = await cluster.CloneAsync(PostgresTemplates.MultiTenant, "degradeddb1", cancellationToken);
        var catalog = await cluster.CloneAsync(PostgresTemplates.Empty, "degradedcatalog", cancellationToken);

        // Starting at all is half the assertion: an unsharded module whose only database is
        // unreachable fails startup, and that is still the right behaviour for it.
        using var host = await StartHostAsync(db1, catalog);

        var health = host.Services.GetRequiredKeyedService<ShardHealth>(ModuleKey);
        Assert.Equal(["db2"], health.Unhealthy);
        Assert.False(string.IsNullOrEmpty(health.Get("db2").LastError));
        Assert.True(health.Get("db1").Healthy);

        // The tenants of the healthy database are unaffected.
        var map = host.Services.GetRequiredKeyedService<ITenantShardMap>(ModuleKey);
        await map.AssignAsync("acme", "db1", cancellationToken);

        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant("acme");
        var store = scope.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);

        var appended = await store.AppendAsync(
            [new EventToPersist
            {
                EventType = new EventType("shard-test-event"),
                Tags = [EventTag.Parse($"degraded:{Guid.NewGuid():N}")],
                EventData = """{"test": true}""",
            }],
            cancellationToken: cancellationToken);

        Assert.Single(appended);

        await host.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task The_health_check_reports_degraded_and_names_the_missing_shard()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var db1 = await cluster.CloneAsync(PostgresTemplates.MultiTenant, "degradedcheckdb1", cancellationToken);
        var catalog = await cluster.CloneAsync(PostgresTemplates.Empty, "degradedcheckcatalog", cancellationToken);

        using var host = await StartHostAsync(db1, catalog, withHealthChecks: true);

        var report = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == $"alberto-shards-{ModuleKey}", cancellationToken);

        var entry = Assert.Single(report.Entries).Value;
        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Equal("healthy", entry.Data["db1"]);
        Assert.NotEqual("healthy", entry.Data["db2"]);

        await host.StopAsync(cancellationToken);
    }
}
