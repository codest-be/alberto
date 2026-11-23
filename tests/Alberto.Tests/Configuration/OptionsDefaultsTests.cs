using Alberto.Configuration;
using Xunit;

namespace Alberto.Tests.Configuration;

public class OptionsDefaultsTests
{
    [Fact]
    public void ControlLoopOptions_defaults_match_the_documented_values()
    {
        var options = new ControlLoopOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(250), options.PollingInterval);
        Assert.Equal(100, options.BatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(100), options.HeadRefreshInterval);
        Assert.Equal(2000, options.HeadWindowSize);
        Assert.Equal(3, options.Retry.MaxRetries);
        Assert.Equal(10, options.DeadLetterRetry.BatchSize);
        Assert.False(options.Leases.Enabled);
    }

    [Fact]
    public void ControlLoopOptions_Default_is_equal_to_a_fresh_instance()
    {
        Assert.Equal(new ControlLoopOptions(), ControlLoopOptions.Default);
    }

    [Fact]
    public void An_empty_override_changes_nothing()
    {
        var options = new ControlLoopOptions { BatchSize = 777 };

        var result = new ControlLoopOverrides().ApplyTo(options);

        Assert.Equal(options, result);
    }

    [Fact]
    public void A_nested_override_replaces_only_the_named_property()
    {
        var options = new ControlLoopOptions();

        var result = new ControlLoopOverrides
        {
            Retry = new RetryOverrides { MaxRetries = 9 },
        }.ApplyTo(options);

        Assert.Equal(9, result.Retry.MaxRetries);
        Assert.Equal(options.Retry.RetryDelay, result.Retry.RetryDelay);
        Assert.Equal(options.BatchSize, result.BatchSize);
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(20, 30000)]
    public void CalculateDelay_backs_off_exponentially_and_caps(int attempt, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), new RetryOptions()
            .CalculateDelay(attempt));
    }

    [Fact]
    public void CheckpointOptions_defaults_to_Warn()
    {
        Assert.Equal(OrphanCheckpointPolicy.Warn, new CheckpointOptions().OrphanPolicy);
    }
}
