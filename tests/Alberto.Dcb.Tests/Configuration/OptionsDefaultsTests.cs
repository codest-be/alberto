using Alberto.Dcb.Configuration;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

public class OptionsDefaultsTests
{
    [Fact]
    public void ControlLoopOptions_defaults_match_the_documented_values()
    {
        var options = new ControlLoopOptions();

        options.PollingInterval.Should().Be(TimeSpan.FromMilliseconds(250));
        options.BatchSize.Should().Be(100);
        options.HeadRefreshInterval.Should().Be(TimeSpan.FromMilliseconds(100));
        options.HeadWindowSize.Should().Be(2000);
        options.Retry.MaxRetries.Should().Be(3);
        options.DeadLetterRetry.BatchSize.Should().Be(10);
        options.Leases.Enabled.Should().BeFalse();
    }

    [Fact]
    public void ControlLoopOptions_Default_is_equal_to_a_fresh_instance()
    {
        ControlLoopOptions.Default.Should().Be(new ControlLoopOptions());
    }

    [Fact]
    public void An_empty_override_changes_nothing()
    {
        var options = new ControlLoopOptions { BatchSize = 777 };

        var result = new ControlLoopOverrides().ApplyTo(options);

        result.Should().Be(options);
    }

    [Fact]
    public void A_nested_override_replaces_only_the_named_property()
    {
        var options = new ControlLoopOptions();

        var result = new ControlLoopOverrides
        {
            Retry = new RetryOverrides { MaxRetries = 9 },
        }.ApplyTo(options);

        result.Retry.MaxRetries.Should().Be(9);
        result.Retry.RetryDelay.Should().Be(options.Retry.RetryDelay);
        result.BatchSize.Should().Be(options.BatchSize);
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(20, 30000)]
    public void CalculateDelay_backs_off_exponentially_and_caps(int attempt, int expectedMilliseconds)
    {
        new RetryOptions()
            .CalculateDelay(attempt)
            .Should().Be(TimeSpan.FromMilliseconds(expectedMilliseconds));
    }

    [Fact]
    public void CheckpointOptions_defaults_to_Warn()
    {
        new CheckpointOptions().OrphanPolicy.Should().Be(OrphanCheckpointPolicy.Warn);
    }
}
