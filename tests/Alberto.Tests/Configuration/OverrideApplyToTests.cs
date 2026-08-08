using Alberto.Configuration;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Guards the ApplyTo implementations on all three overlay classes.
/// For each property there must be a "set value wins" test (kills null-coalescing remove-left/right mutants)
/// AND a "null preserves base" test (kills statement-removal mutants and confirms null-coalescing semantics).
/// </summary>
public class OverrideApplyToTests
{
    // ─── RetryOverrides ──────────────────────────────────────────────────────

    [Fact]
    public void RetryOverrides_null_argument_throws()
    {
        var act = () => new RetryOverrides().ApplyTo(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RetryOverrides_empty_overlay_leaves_every_property_intact()
    {
        var base_ = new RetryOptions
        {
            MaxRetries = 7,
            RetryDelay = TimeSpan.FromSeconds(5),
            BackoffMultiplier = 3.0,
            MaxRetryDelay = TimeSpan.FromMinutes(2),
            DeadLetterOnMaxRetries = false,
        };

        var result = new RetryOverrides().ApplyTo(base_);

        result.Should().Be(base_);
    }

    [Fact]
    public void RetryOverrides_set_MaxRetries_wins_over_base()
    {
        var base_ = new RetryOptions { MaxRetries = 3 };
        var result = new RetryOverrides { MaxRetries = 10 }.ApplyTo(base_);

        result.MaxRetries.Should().Be(10);
    }

    [Fact]
    public void RetryOverrides_null_MaxRetries_preserves_base()
    {
        var base_ = new RetryOptions { MaxRetries = 7 };
        var result = new RetryOverrides { MaxRetries = null }.ApplyTo(base_);

        result.MaxRetries.Should().Be(7);
    }

    /// <summary>Kills L62 "Null coalescing (remove left)" — RetryDelay ?? options.RetryDelay → options.RetryDelay.</summary>
    [Fact]
    public void RetryOverrides_set_RetryDelay_wins_over_base()
    {
        var base_ = new RetryOptions { RetryDelay = TimeSpan.FromSeconds(1) };
        var result = new RetryOverrides { RetryDelay = TimeSpan.FromSeconds(5) }.ApplyTo(base_);

        result.RetryDelay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RetryOverrides_null_RetryDelay_preserves_base()
    {
        var base_ = new RetryOptions { RetryDelay = TimeSpan.FromSeconds(3) };
        var result = new RetryOverrides { RetryDelay = null }.ApplyTo(base_);

        result.RetryDelay.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void RetryOverrides_set_BackoffMultiplier_wins_over_base()
    {
        var base_ = new RetryOptions { BackoffMultiplier = 2.0 };
        var result = new RetryOverrides { BackoffMultiplier = 4.0 }.ApplyTo(base_);

        result.BackoffMultiplier.Should().Be(4.0);
    }

    [Fact]
    public void RetryOverrides_set_MaxRetryDelay_wins_over_base()
    {
        var base_ = new RetryOptions { MaxRetryDelay = TimeSpan.FromMinutes(1) };
        var result = new RetryOverrides { MaxRetryDelay = TimeSpan.FromMinutes(5) }.ApplyTo(base_);

        result.MaxRetryDelay.Should().Be(TimeSpan.FromMinutes(5));
    }

    /// <summary>Kills L65 "Null coalescing (remove left)" — DeadLetterOnMaxRetries ?? options.DeadLetterOnMaxRetries → options.DeadLetterOnMaxRetries.</summary>
    [Fact]
    public void RetryOverrides_set_DeadLetterOnMaxRetries_wins_over_base()
    {
        // Base has true; override flips it to false. The remove-left mutant ignores the override
        // and always returns options.DeadLetterOnMaxRetries (true), so this assertion fails.
        var base_ = new RetryOptions { DeadLetterOnMaxRetries = true };
        var result = new RetryOverrides { DeadLetterOnMaxRetries = false }.ApplyTo(base_);

        result.DeadLetterOnMaxRetries.Should().BeFalse();
    }

    [Fact]
    public void RetryOverrides_null_DeadLetterOnMaxRetries_preserves_base()
    {
        var base_ = new RetryOptions { DeadLetterOnMaxRetries = false };
        var result = new RetryOverrides { DeadLetterOnMaxRetries = null }.ApplyTo(base_);

        result.DeadLetterOnMaxRetries.Should().BeFalse();
    }

    [Fact]
    public void RetryOverrides_only_named_properties_change()
    {
        var base_ = new RetryOptions
        {
            MaxRetries = 3,
            RetryDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxRetryDelay = TimeSpan.FromSeconds(30),
            DeadLetterOnMaxRetries = true,
        };

        var result = new RetryOverrides { MaxRetries = 9 }.ApplyTo(base_);

        result.MaxRetries.Should().Be(9);
        result.RetryDelay.Should().Be(base_.RetryDelay, "untouched");
        result.BackoffMultiplier.Should().Be(base_.BackoffMultiplier, "untouched");
        result.MaxRetryDelay.Should().Be(base_.MaxRetryDelay, "untouched");
        result.DeadLetterOnMaxRetries.Should().Be(base_.DeadLetterOnMaxRetries, "untouched");
    }

    // ─── DeadLetterRetryOverrides ────────────────────────────────────────────

    /// <summary>Kills L100 NoCoverage Statement mutation — ArgumentNullException.ThrowIfNull(options) → ';'.</summary>
    [Fact]
    public void DeadLetterRetryOverrides_null_argument_throws()
    {
        var act = () => new DeadLetterRetryOverrides().ApplyTo(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DeadLetterRetryOverrides_empty_overlay_leaves_every_property_intact()
    {
        var base_ = new DeadLetterRetryOptions
        {
            PollingInterval = TimeSpan.FromSeconds(30),
            BatchSize = 5,
            ClaimLease = TimeSpan.FromMinutes(10),
        };

        var result = new DeadLetterRetryOverrides().ApplyTo(base_);

        result.Should().Be(base_);
    }

    /// <summary>Kills L104 NoCoverage "Null coalescing (remove left)" — PollingInterval ?? options.PollingInterval → options.PollingInterval.</summary>
    [Fact]
    public void DeadLetterRetryOverrides_set_PollingInterval_wins_over_base()
    {
        var base_ = new DeadLetterRetryOptions { PollingInterval = TimeSpan.FromMinutes(1) };
        var result = new DeadLetterRetryOverrides { PollingInterval = TimeSpan.FromSeconds(30) }.ApplyTo(base_);

        result.PollingInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void DeadLetterRetryOverrides_null_PollingInterval_preserves_base()
    {
        var base_ = new DeadLetterRetryOptions { PollingInterval = TimeSpan.FromSeconds(45) };
        var result = new DeadLetterRetryOverrides { PollingInterval = null }.ApplyTo(base_);

        result.PollingInterval.Should().Be(TimeSpan.FromSeconds(45));
    }

    /// <summary>Kills L105 NoCoverage "Null coalescing (remove left)" — BatchSize ?? options.BatchSize → options.BatchSize.</summary>
    [Fact]
    public void DeadLetterRetryOverrides_set_BatchSize_wins_over_base()
    {
        var base_ = new DeadLetterRetryOptions { BatchSize = 10 };
        var result = new DeadLetterRetryOverrides { BatchSize = 3 }.ApplyTo(base_);

        result.BatchSize.Should().Be(3);
    }

    [Fact]
    public void DeadLetterRetryOverrides_null_BatchSize_preserves_base()
    {
        var base_ = new DeadLetterRetryOptions { BatchSize = 7 };
        var result = new DeadLetterRetryOverrides { BatchSize = null }.ApplyTo(base_);

        result.BatchSize.Should().Be(7);
    }

    /// <summary>Kills L106 NoCoverage "Null coalescing (remove left)" — ClaimLease ?? options.ClaimLease → options.ClaimLease.</summary>
    [Fact]
    public void DeadLetterRetryOverrides_set_ClaimLease_wins_over_base()
    {
        var base_ = new DeadLetterRetryOptions { ClaimLease = TimeSpan.FromMinutes(15) };
        var result = new DeadLetterRetryOverrides { ClaimLease = TimeSpan.FromMinutes(5) }.ApplyTo(base_);

        result.ClaimLease.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void DeadLetterRetryOverrides_null_ClaimLease_preserves_base()
    {
        var base_ = new DeadLetterRetryOptions { ClaimLease = TimeSpan.FromMinutes(20) };
        var result = new DeadLetterRetryOverrides { ClaimLease = null }.ApplyTo(base_);

        result.ClaimLease.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void DeadLetterRetryOverrides_only_named_properties_change()
    {
        var base_ = new DeadLetterRetryOptions
        {
            PollingInterval = TimeSpan.FromMinutes(1),
            BatchSize = 10,
            ClaimLease = TimeSpan.FromMinutes(15),
        };

        var result = new DeadLetterRetryOverrides { BatchSize = 20 }.ApplyTo(base_);

        result.BatchSize.Should().Be(20);
        result.PollingInterval.Should().Be(base_.PollingInterval, "untouched");
        result.ClaimLease.Should().Be(base_.ClaimLease, "untouched");
    }

    // ─── ProcessorLeaseOverrides ──────────────────────────────────────────────

    [Fact]
    public void ProcessorLeaseOverrides_null_argument_throws()
    {
        var act = () => new ProcessorLeaseOverrides().ApplyTo(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProcessorLeaseOverrides_empty_overlay_leaves_every_property_intact()
    {
        var base_ = new ProcessorLeaseOptions { Enabled = true, ReplicaId = "pod-1" };
        var result = new ProcessorLeaseOverrides().ApplyTo(base_);

        result.Should().Be(base_);
    }

    /// <summary>
    /// Kills L148 Survived Statement mutation — removes "Enabled = Enabled ?? options.Enabled".
    /// Without the statement, Enabled keeps the base value even when the override sets it.
    /// </summary>
    [Fact]
    public void ProcessorLeaseOverrides_set_Enabled_wins_over_base()
    {
        var base_ = new ProcessorLeaseOptions { Enabled = false };
        var result = new ProcessorLeaseOverrides { Enabled = true }.ApplyTo(base_);

        result.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ProcessorLeaseOverrides_null_Enabled_preserves_base()
    {
        var base_ = new ProcessorLeaseOptions { Enabled = true };
        var result = new ProcessorLeaseOverrides { Enabled = null }.ApplyTo(base_);

        result.Enabled.Should().BeTrue();
    }

    /// <summary>
    /// Kills L153 Survived "Null coalescing (remove right)" — ReplicaId ?? options.ReplicaId → ReplicaId.
    /// The mutant returns null when the override's ReplicaId is null, losing the base value.
    /// This is the C5 change: the overlay must fall back to the base's ReplicaId when not set.
    /// </summary>
    [Fact]
    public void ProcessorLeaseOverrides_null_ReplicaId_preserves_base_ReplicaId()
    {
        var base_ = new ProcessorLeaseOptions { ReplicaId = "pod-1" };
        var result = new ProcessorLeaseOverrides { ReplicaId = null }.ApplyTo(base_);

        result.ReplicaId.Should().Be("pod-1");
    }

    [Fact]
    public void ProcessorLeaseOverrides_set_ReplicaId_wins_over_base()
    {
        var base_ = new ProcessorLeaseOptions { ReplicaId = "pod-1" };
        var result = new ProcessorLeaseOverrides { ReplicaId = "pod-2" }.ApplyTo(base_);

        result.ReplicaId.Should().Be("pod-2");
    }

    [Fact]
    public void ProcessorLeaseOverrides_only_named_properties_change()
    {
        var base_ = new ProcessorLeaseOptions { Enabled = true, ReplicaId = "pod-1" };
        var result = new ProcessorLeaseOverrides { Enabled = false }.ApplyTo(base_);

        result.Enabled.Should().BeFalse();
        result.ReplicaId.Should().Be("pod-1", "untouched");
    }
}
