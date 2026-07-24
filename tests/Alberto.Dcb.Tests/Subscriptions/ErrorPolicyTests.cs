using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

public class ErrorPolicyTests
{
    #region Default Values

    [Fact]
    public void Default_ShouldHaveExpectedValues()
    {
        var policy = ErrorPolicy.Default;

        Assert.Equal(3, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.RetryDelay);
        Assert.Equal(2.0, policy.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.MaxRetryDelay);
        Assert.True(policy.DeadLetterOnMaxRetries);
        Assert.NotNull(policy.ErrorClassifier);
    }

    #endregion

    #region CalculateDelay Tests

    [Fact]
    public void CalculateDelay_FirstAttempt_ShouldReturnBaseDelay()
    {
        var policy = new ErrorPolicy { RetryDelay = TimeSpan.FromSeconds(1) };

        var delay = policy.CalculateDelay(1);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void CalculateDelay_ZeroAttempt_ShouldReturnBaseDelay()
    {
        var policy = new ErrorPolicy { RetryDelay = TimeSpan.FromSeconds(1) };

        var delay = policy.CalculateDelay(0);

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
        var policy = new ErrorPolicy
        {
            RetryDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxRetryDelay = TimeSpan.FromMinutes(5) // High max to not cap
        };

        var delay = policy.CalculateDelay(attempt);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), delay);
    }

    [Fact]
    public void CalculateDelay_ShouldCapAtMaxRetryDelay()
    {
        var policy = new ErrorPolicy
        {
            RetryDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };

        // Attempt 10 would be 1 * 2^9 = 512 seconds, but should cap at 5 seconds
        var delay = policy.CalculateDelay(10);

        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }

    [Fact]
    public void CalculateDelay_WithMultiplierOne_ShouldReturnConstantDelay()
    {
        var policy = new ErrorPolicy
        {
            RetryDelay = TimeSpan.FromSeconds(2),
            BackoffMultiplier = 1.0
        };

        Assert.Equal(TimeSpan.FromSeconds(2), policy.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.CalculateDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.CalculateDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.CalculateDelay(10));
    }

    [Fact]
    public void CalculateDelay_WithCustomBaseDelay_ShouldUseIt()
    {
        var policy = new ErrorPolicy
        {
            RetryDelay = TimeSpan.FromMilliseconds(500),
            BackoffMultiplier = 2.0,
            MaxRetryDelay = TimeSpan.FromMinutes(5)
        };

        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), policy.CalculateDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), policy.CalculateDelay(3));
    }

    [Fact]
    public void CalculateDelay_WithFractionalMultiplier_ShouldCalculateCorrectly()
    {
        var policy = new ErrorPolicy
        {
            RetryDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 1.5,
            MaxRetryDelay = TimeSpan.FromMinutes(5)
        };

        // 1.5^1 = 1.5, 1.5^2 = 2.25, 1.5^3 = 3.375
        Assert.Equal(TimeSpan.FromSeconds(1), policy.CalculateDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(1.5), policy.CalculateDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(2.25), policy.CalculateDelay(3));
    }

    [Fact]
    public void CalculateDelay_AttemptExceedingMax_ShouldCapAtMax()
    {
        var policy = new ErrorPolicy
        {
            RetryDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxRetryDelay = TimeSpan.FromSeconds(30)
        };

        // Attempt 10 = 1 * 2^9 = 512 seconds, should cap at 30 seconds
        var delay = policy.CalculateDelay(10);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    #endregion

    #region Configuration Tests

    [Fact]
    public void Policy_ShouldAllowCustomConfiguration()
    {
        var customClassifier = new TestErrorClassifier();
        var policy = new ErrorPolicy
        {
            MaxRetries = 5,
            RetryDelay = TimeSpan.FromMilliseconds(100),
            BackoffMultiplier = 3.0,
            MaxRetryDelay = TimeSpan.FromSeconds(10),
            DeadLetterOnMaxRetries = false,
            ErrorClassifier = customClassifier
        };

        Assert.Equal(5, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.RetryDelay);
        Assert.Equal(3.0, policy.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.MaxRetryDelay);
        Assert.False(policy.DeadLetterOnMaxRetries);
        Assert.Same(customClassifier, policy.ErrorClassifier);
    }

    [Fact]
    public void MaxRetries_Zero_IsAllowed()
    {
        // Zero means "attempt once, never retry" - the attempt loop still runs.
        var policy = new ErrorPolicy { MaxRetries = 0 };

        Assert.Equal(0, policy.MaxRetries);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void MaxRetries_Negative_Throws(int maxRetries)
    {
        // A negative value would skip the attempt loop entirely: the event would be
        // neither dispatched, retried nor dead-lettered, and a multi-event batch would
        // be reported as successfully processed. Reject it at construction instead.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ErrorPolicy { MaxRetries = maxRetries });

        Assert.Equal(nameof(ErrorPolicy.MaxRetries), ex.ParamName);
    }

    #endregion

    #region Test Helpers

    private sealed class TestErrorClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception) => ErrorClassification.Unknown;
    }

    #endregion
}
