using Alberto.Dcb.Tenancy;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Dcb.Tests.Tenancy;

public class ShardHealthTests
{
    private static readonly string[] TwoShards = ["db1", "db2"];

    [Fact]
    public void A_shard_nothing_has_reported_on_is_healthy()
    {
        // Only failures report, so silence is the normal case and must not read as an outage.
        new ShardHealth().Get("db1").Healthy.Should().BeTrue();
    }

    [Fact]
    public void A_failure_is_recorded_with_its_message()
    {
        var health = new ShardHealth();

        health.Report("db1", new InvalidOperationException("connection refused"));

        var state = health.Get("db1");
        state.Healthy.Should().BeFalse();
        state.LastError.Should().Be("connection refused");
        health.Unhealthy.Should().Equal("db1");
    }

    [Fact]
    public void A_recovery_clears_the_error()
    {
        var health = new ShardHealth();
        health.Report("db1", new InvalidOperationException("connection refused"));

        health.Report("db1", null);

        health.Get("db1").Healthy.Should().BeTrue();
        health.Get("db1").LastError.Should().BeNull();
        health.Unhealthy.Should().BeEmpty();
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

        health.Get("db1").LastChangedAt.Should().Be(brokenAt);
        health.Get("db1").LastError.Should().Be("still broken");
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

        health.Get("db1").LastChangedAt.Should().Be(brokenAt + TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Reporting_one_shard_leaves_the_others_alone()
    {
        var health = new ShardHealth();

        health.Report("db1", new InvalidOperationException("down"));

        health.Get("db2").Healthy.Should().BeTrue();
        health.All.Should().ContainKey("db1").And.NotContainKey("db2");
    }

    [Fact]
    public async Task The_health_check_is_healthy_while_every_shard_is_up()
    {
        var result = await Check(new ShardHealth());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task One_shard_down_degrades_rather_than_fails()
    {
        // A load balancer pulling the instance out over one dead database would spread the outage
        // to the tenants that were fine.
        var health = new ShardHealth();
        health.Report("db1", new InvalidOperationException("connection refused"));

        var result = await Check(health);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("db1");
        result.Data["db1"].Should().Be("connection refused");
        result.Data["db2"].Should().Be("healthy");
    }

    [Fact]
    public async Task Every_shard_down_is_unhealthy()
    {
        var health = new ShardHealth();
        health.Report("db1", new InvalidOperationException("down"));
        health.Report("db2", new InvalidOperationException("down"));

        (await Check(health)).Status.Should().Be(HealthStatus.Unhealthy);
    }

    private static Task<HealthCheckResult> Check(ShardHealth health) =>
        new ShardHealthCheck("orders", health, TwoShards)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
}
