using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for <see cref="Alberto.Dcb.Configuration.RetryOptions"/> (formerly ErrorPolicy,
/// which was deleted in the 1.0 configuration refactor).
/// </summary>
public class ErrorPolicyTests
{
    #region Default Values

    [Fact]
    public void Default_ShouldHaveExpectedValues()
    {
        var retry = new RetryOptions();

        Assert.Equal(3, retry.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(1), retry.RetryDelay);
        Assert.Equal(2.0, retry.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(30), retry.MaxRetryDelay);
        Assert.True(retry.DeadLetterOnMaxRetries);
    }

    #endregion

    #region CalculateDelay Tests

    [Fact]
    public void CalculateDelay_FirstAttempt_ShouldReturnBaseDelay()
    {
        var retry = new RetryOptions { RetryDelay = TimeSpan.FromSeconds(1) };

        var delay = retry.CalculateDelay(1);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void CalculateDelay_ZeroAttempt_ShouldReturnBaseDelay()
    {
        var retry = new RetryOptions { RetryDelay = TimeSpan.FromSeconds(1) };

        var delay = retry.CalculateDelay(0);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Theory]
    [InlineData(1, 1000)]   // 1 second
    [InlineData(2, 2000)]   // 1 * 2^1 = 2 seconds
    [InlineData(3, 4000)]   // 1 * 2^2 = 4 seconds
    [InlineData(4, 8000)]   // 1 * 2^3 = 8 seconds
    [InlineData(5, 16000)]  // 1 * 2^4 = 16 seconds
    public void CalculateDelay_ExponentialBackoff_ShouldDoubleEachAttempt(int attempt, double expectedMs)
    {
        var retry = new RetryOptions
        {
            RetryDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxRetryDelay = TimeSpan.FromMinutes(5) // High max to not cap
        };

        var delay = retry.CalculateDelay(attempt);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), delay);
    }

    [Fact]
    public void CalculateDelay_ShouldCapAtMaxRetryDelay()
    {
        var retry = new RetryOptions
        {
            RetryDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };

        // Attempt 10 would be 1 * 2^9 = 512 seconds, but should cap at 5 seconds
        var delay = retry.CalculateDelay(10);

        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }

    [Fact]
    public void CalculateDelay_WithMultiplierOne_ShouldReturnConstantDelay()
    {
        var retry = new RetryOptions
        {
            RetryDelay = TimeSpan.FromSeconds(2),
            BackoffMultiplier = 1.0
        };

        Assert.Equal(TimeSpan.FromSeconds(2), retry.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), retry.CalculateDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(2), retry.CalculateDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(2), retry.CalculateDelay(10));
    }

    [Fact]
    public void CalculateDelay_WithCustomBaseDelay_ShouldUseIt()
    {
        var retry = new RetryOptions
        {
            RetryDelay = TimeSpan.FromMilliseconds(500),
            BackoffMultiplier = 2.0,
            MaxRetryDelay = TimeSpan.FromMinutes(5)
        };

        Assert.Equal(TimeSpan.FromMilliseconds(500), retry.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), retry.CalculateDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), retry.CalculateDelay(3));
    }

    [Fact]
    public void CalculateDelay_WithFractionalMultiplier_ShouldCalculateCorrectly()
    {
        var retry = new RetryOptions
        {
            RetryDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 1.5,
            MaxRetryDelay = TimeSpan.FromMinutes(5)
        };

        // 1.5^1 = 1.5, 1.5^2 = 2.25, 1.5^3 = 3.375
        Assert.Equal(TimeSpan.FromSeconds(1), retry.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(1.5), retry.CalculateDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(2.25), retry.CalculateDelay(3));
    }

    [Fact]
    public void CalculateDelay_AttemptExceedingMax_ShouldCapAtMax()
    {
        var retry = new RetryOptions
        {
            RetryDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxRetryDelay = TimeSpan.FromSeconds(30)
        };

        // Attempt 10 = 1 * 2^9 = 512 seconds, should cap at 30 seconds
        var delay = retry.CalculateDelay(10);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    #endregion

    #region Configuration Tests

    [Fact]
    public void RetryOptions_ShouldAllowCustomConfiguration()
    {
        var retry = new RetryOptions
        {
            MaxRetries = 5,
            RetryDelay = TimeSpan.FromMilliseconds(100),
            BackoffMultiplier = 3.0,
            MaxRetryDelay = TimeSpan.FromSeconds(10),
            DeadLetterOnMaxRetries = false,
        };

        Assert.Equal(5, retry.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(100), retry.RetryDelay);
        Assert.Equal(3.0, retry.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(10), retry.MaxRetryDelay);
        Assert.False(retry.DeadLetterOnMaxRetries);
    }

    [Fact]
    public void MaxRetries_Zero_IsAllowed()
    {
        // Zero means "attempt once, never retry" - the attempt loop still runs.
        var retry = new RetryOptions { MaxRetries = 0 };

        Assert.Equal(0, retry.MaxRetries);
    }

    // MaxRetries < 0 is now rejected at module startup by AlbertoModuleValidator (ALB0007)
    // rather than at construction time. See AlbertoModuleValidatorTests.A_negative_retry_count_fails_with_ALB0007.

    #endregion

}
