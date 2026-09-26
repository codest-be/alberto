using Alberto.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Fast (no Docker) tests for <see cref="EventStoreHead"/>'s stall detection: the warning
/// logged once the stable-head barrier has held the head back past the configured threshold,
/// the information logged once it clears, and the <c>alberto.head.stalled</c> gauge that backs
/// both. Uses <see cref="FakeTimeProvider"/> so elapsed time is asserted exactly rather than
/// approximated with real delays.
/// </summary>
public sealed class EventStoreHeadStallDetectionTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task BarrierHolding_UnderThreshold_NoWarningAndGaugeStaysZero()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<EventStoreHead>();
        var backend = new FakeHeadBackend { Positions = [1, 2, 3], StableHead = 1 };
        var head = new EventStoreHead(backend,
            logger: logger, timeProvider: time, moduleKey: "m1", stallWarningThreshold: Threshold);
        var ct = TestContext.Current.CancellationToken;

        await head.RefreshAsync(ct); // first poll: head moves 0 -> 1, not yet "stalled"
        await head.RefreshAsync(ct); // second poll: head stays at 1 — the stall clock starts here

        // Advance less than the threshold and poll again without anything else changing.
        time.Advance(TimeSpan.FromSeconds(10));
        await head.RefreshAsync(ct);

        Assert.Equal(1, head.Current);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        // A hold under the threshold is routine (a normal short-lived write transaction), so
        // the gauge must not show it yet either — only the warning's own threshold matters.
        AssertGauge("m1", g => g == 0);
    }

    [Fact]
    public async Task BarrierHolding_PastThreshold_WarnsOnceAndGaugePositive()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<EventStoreHead>();
        var backend = new FakeHeadBackend { Positions = [1, 2, 3], StableHead = 1 };
        var head = new EventStoreHead(backend,
            logger: logger, timeProvider: time, moduleKey: "m2", stallWarningThreshold: Threshold);
        var ct = TestContext.Current.CancellationToken;

        await head.RefreshAsync(ct); // head moves 0 -> 1
        await head.RefreshAsync(ct); // head stays at 1 — the stall clock starts here

        time.Advance(Threshold + TimeSpan.FromSeconds(1));
        await head.RefreshAsync(ct);

        Assert.Equal(1, head.Current);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        AssertGauge("m2", g => g > 0);

        // Polling again while still stalled must not warn a second time.
        time.Advance(TimeSpan.FromSeconds(5));
        await head.RefreshAsync(ct);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task BarrierReleases_AfterWarning_LogsInformationAndGaugeReturnsToZero()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<EventStoreHead>();
        var backend = new FakeHeadBackend { Positions = [1, 2, 3], StableHead = 1 };
        var head = new EventStoreHead(backend,
            logger: logger, timeProvider: time, moduleKey: "m3", stallWarningThreshold: Threshold);
        var ct = TestContext.Current.CancellationToken;

        await head.RefreshAsync(ct); // head moves 0 -> 1
        await head.RefreshAsync(ct); // head stays at 1 — the stall clock starts here

        time.Advance(Threshold + TimeSpan.FromSeconds(1));
        await head.RefreshAsync(ct);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);

        // The barrier releases: the backend now lets the head all the way through.
        backend.StableHead = long.MaxValue;
        await head.RefreshAsync(ct);

        Assert.Equal(3, head.Current);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        AssertGauge("m3", g => g == 0);
    }

    [Fact]
    public async Task HeadAdvancingWithBriefHolds_NeverWarns()
    {
        // A barrier that keeps moving forward — never pinned at the same position for two
        // consecutive polls — is normal operation, not a stall, however long the test runs.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<EventStoreHead>();
        var backend = new FakeHeadBackend { Positions = [1, 2, 3, 4, 5], StableHead = 1 };
        var head = new EventStoreHead(backend,
            logger: logger, timeProvider: time, moduleKey: "m4", stallWarningThreshold: Threshold);
        var ct = TestContext.Current.CancellationToken;

        await head.RefreshAsync(ct); // holds at 1

        for (var stableHead = 2; stableHead <= 5; stableHead++)
        {
            time.Advance(Threshold + TimeSpan.FromSeconds(1)); // would trip the threshold if stalled
            backend.StableHead = stableHead;
            await head.RefreshAsync(ct);
        }

        Assert.Equal(5, head.Current);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        AssertGauge("m4", g => g == 0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void AssertGauge(string moduleKey, Func<double, bool> predicate)
    {
        var measurements = new List<double>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Alberto.Telemetry.AlbertoMetrics.Name
                && instrument.Name == "alberto.head.stalled")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "module" && Equals(tag.Value, moduleKey))
                {
                    measurements.Add(measurement);
                    return;
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.True(measurements.Count > 0, $"no measurement recorded for module '{moduleKey}'");
        Assert.True(predicate(measurements[^1]),
            $"gauge value {measurements[^1]} for module '{moduleKey}' did not match expectation");
    }

    private sealed class FakeHeadBackend : IEventStoreHeadBackend
    {
        public IReadOnlyList<long> Positions { get; set; } = [];
        public long StableHead { get; set; } = long.MaxValue;

        public Task<IReadOnlyList<long>> GetPositionsAsync(
            long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        {
            var ceiling = afterPosition + windowSize;
            IReadOnlyList<long> window = Positions
                .Where(p => p > afterPosition && p <= ceiling)
                .OrderBy(p => p)
                .ToList();
            return Task.FromResult(window);
        }

        public Task<long> GetStableHeadAsync(long afterPosition, CancellationToken cancellationToken = default)
            => Task.FromResult(StableHead);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];
        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
