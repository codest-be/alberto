using Alberto.Postgres;
using Alberto.Tenancy;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
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
        var ct = TestContext.Current.CancellationToken;
        var db1 = await cluster.CloneAsync(PostgresTemplates.MultiTenant, "degradeddb1", ct);
        var catalog = await cluster.CloneAsync(PostgresTemplates.Empty, "degradedcatalog", ct);

        // Starting at all is half the assertion: an unsharded module whose only database is
        // unreachable fails startup, and that is still the right behaviour for it.
        using var host = await StartHostAsync(db1, catalog);

        var health = host.Services.GetRequiredKeyedService<ShardHealth>(ModuleKey);
        health.Unhealthy.Should().Equal("db2");
        health.Get("db2").LastError.Should().NotBeNullOrEmpty();
        health.Get("db1").Healthy.Should().BeTrue();

        // The tenants of the healthy database are unaffected.
        var map = host.Services.GetRequiredKeyedService<ITenantShardMap>(ModuleKey);
        await map.AssignAsync("acme", "db1", ct);

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
            cancellationToken: ct);

        appended.Should().ContainSingle();

        await host.StopAsync(ct);
    }

    [Fact]
    public async Task The_health_check_reports_degraded_and_names_the_missing_shard()
    {
        var ct = TestContext.Current.CancellationToken;
        var db1 = await cluster.CloneAsync(PostgresTemplates.MultiTenant, "degradedcheckdb1", ct);
        var catalog = await cluster.CloneAsync(PostgresTemplates.Empty, "degradedcheckcatalog", ct);

        using var host = await StartHostAsync(db1, catalog, withHealthChecks: true);

        var report = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == $"alberto-shards-{ModuleKey}", ct);

        var entry = report.Entries.Should().ContainSingle().Subject.Value;
        entry.Status.Should().Be(HealthStatus.Degraded);
        entry.Data["db1"].Should().Be("healthy");
        entry.Data["db2"].Should().NotBe("healthy");

        await host.StopAsync(ct);
    }
}
