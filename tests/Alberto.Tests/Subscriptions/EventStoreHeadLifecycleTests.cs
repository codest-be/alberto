using Alberto.Subscriptions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// The background refresh loop of <see cref="EventStoreHead"/>: that it runs, that
/// <see cref="EventStoreHead.StopAsync"/> stops it, and that neither a failing warm-up nor a
/// backend that ignores cancellation takes the host down with it.
/// </summary>
public sealed class EventStoreHeadLifecycleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task Loop_AdvancesTheHeadOnItsOwn()
    {
        var backend = new ScriptedBackend();
        var head = new EventStoreHead(backend, Interval);
        await head.StartAsync(TestContext.Current.CancellationToken);

        backend.Positions = [1, 2];

        await WaitUntilAsync(() => head.Current == 2);
        await head.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_HaltsTheLoop()
    {
        var backend = new ScriptedBackend();
        var head = new EventStoreHead(backend, Interval);
        await head.StartAsync(TestContext.Current.CancellationToken);

        await head.StopAsync(CancellationToken.None);
        backend.Positions = [1];
        await Task.Delay(Interval * 20, TestContext.Current.CancellationToken);

        Assert.Equal(0, head.Current);
    }

    [Fact]
    public async Task WarmUpFailure_StartsColdAndLogs()
    {
        var logger = new CapturingLogger();
        var backend = new ScriptedBackend { FailFirstCall = true };
        var head = new EventStoreHead(backend, Interval, logger: logger);

        await head.StartAsync(TestContext.Current.CancellationToken);
        await head.StopAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("warm-up"));
    }

    [Fact]
    public async Task Stop_BackendIgnoringCancellation_AbandonsTheWaitAfterDrainTimeout()
    {
        var logger = new CapturingLogger();
        var backend = new ScriptedBackend { HangAfterFirstCall = true };
        var head = new EventStoreHead(backend, Interval, logger: logger,
            drainTimeout: TimeSpan.FromMilliseconds(50));
        await head.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => backend.Hanging);

        await head.StopAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("did not stop"));
        backend.Release();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached within 5s");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private sealed class ScriptedBackend : IEventStoreHeadBackend
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public volatile IReadOnlyList<long> Positions = [];
        public bool FailFirstCall { get; init; }
        public bool HangAfterFirstCall { get; init; }
        public volatile bool Hanging;

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<long>> GetPositionsAsync(
            long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (FailFirstCall && call == 1)
                throw new InvalidOperationException("backend unreachable");
            if (HangAfterFirstCall && call > 1)
            {
                Hanging = true;
                await _release.Task; // deliberately ignores cancellation
            }

            return Positions.Where(p => p > afterPosition).OrderBy(p => p).ToList();
        }

        public Task<long> GetStableHeadAsync(long afterPosition, CancellationToken cancellationToken = default)
            => Task.FromResult(long.MaxValue);
    }

    private sealed class CapturingLogger : ILogger<EventStoreHead>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
