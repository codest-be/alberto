using Alberto.Tenancy;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Tenancy;

public class ShardHealthTests
{
    private static readonly string[] TwoShards = ["db1", "db2"];

    [Fact]
    public void A_shard_nothing_has_reported_on_is_healthy()
    {
        // Only failures report, so silence is the normal case and must not read as an outage.
        Assert.True(new ShardHealth().Get("db1").Healthy);
    }

    [Fact]
    public void A_failure_is_recorded_with_its_message()
    {
        var health = new ShardHealth();

        health.Report("db1", new InvalidOperationException("connection refused"));

        var state = health.Get("db1");
        Assert.False(state.Healthy);
        Assert.Equal("connection refused", state.LastError);
        Assert.Equal(["db1"], health.Unhealthy);
    }

    [Fact]
    public void A_recovery_clears_the_error()
    {
        var health = new ShardHealth();
        health.Report("db1", new InvalidOperationException("connection refused"));

        health.Report("db1", null);

        Assert.True(health.Get("db1").Healthy);
        Assert.Null(health.Get("db1").LastError);
        Assert.Empty(health.Unhealthy);
    }

    [Fact]
    public void A_repeated_failure_keeps_the_timestamp_of_the_first_one()
    {
        // LastChangedAt has to answer "how long has this been broken", not "when did we last look".
        var time = new FakeTimeProvider();
        var health = new ShardHealth(time);

        health.Report("db1", new InvalidOperationException("first"));
        var brokenAt = health.Get("db1").LastChangedAt;

        time.Advance(TimeSpan.FromMinutes(5));
        health.Report("db1", new InvalidOperationException("still broken"));

        Assert.Equal(brokenAt, health.Get("db1").LastChangedAt);
        Assert.Equal("still broken", health.Get("db1").LastError);
    }

    [Fact]
    public void A_state_change_moves_the_timestamp()
    {
        var time = new FakeTimeProvider();
        var health = new ShardHealth(time);
        health.Report("db1", new InvalidOperationException("down"));
        var brokenAt = health.Get("db1").LastChangedAt;

        time.Advance(TimeSpan.FromMinutes(5));
        health.Report("db1", null);

        Assert.Equal(brokenAt + TimeSpan.FromMinutes(5), health.Get("db1").LastChangedAt);
    }

    [Fact]
    public void Reporting_one_shard_leaves_the_others_alone()
    {
        var health = new ShardHealth();

        health.Report("db1", new InvalidOperationException("down"));

        Assert.True(health.Get("db2").Healthy);
        Assert.Contains("db1", health.All);
        Assert.DoesNotContain("db2", health.All);
    }

    [Fact]
    public async Task The_health_check_is_healthy_while_every_shard_is_up()
    {
        var result = await Check(new ShardHealth());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task One_shard_down_degrades_rather_than_fails()
    {
        // A load balancer pulling the instance out over one dead database would spread the outage
        // to the tenants that were fine.
        var health = new ShardHealth();
        health.Report("db1", new InvalidOperationException("connection refused"));

        var result = await Check(health);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("db1", result.Description);
        Assert.Equal("connection refused", result.Data["db1"]);
        Assert.Equal("healthy", result.Data["db2"]);
    }

    [Fact]
    public async Task Every_shard_down_is_unhealthy()
    {
        var health = new ShardHealth();
        health.Report("db1", new InvalidOperationException("down"));
        health.Report("db2", new InvalidOperationException("down"));

        Assert.Equal(HealthStatus.Unhealthy, (await Check(health)).Status);
    }

    private static Task<HealthCheckResult> Check(ShardHealth health) =>
        new ShardHealthCheck("orders", health, TwoShards)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
}
