using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// <see cref="EventStoreHead"/> must catch up in one refresh (not one window per poll) and
/// must cross a permanent gap wider than the window.
/// </summary>
public sealed class EventStoreHeadCatchUpTests
{
    /// <summary>Honours the row-count contract: the next <c>windowSize</c> positions after the cursor.</summary>
    private sealed class RowCountBackend(IReadOnlyList<long> positions) : IEventStoreHeadBackend
    {
        public Task<IReadOnlyList<long>> GetPositionsAsync(
            long afterPosition, int windowSize, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>(
                positions.Where(p => p > afterPosition).OrderBy(p => p).Take(windowSize).ToList());
    }

    [Fact]
    public async Task Refresh_KeepsAdvancingWhileWindowsComeBackFull()
    {
        var backend = new RowCountBackend(Enumerable.Range(1, 95).Select(i => (long)i).ToList());
        var head = new EventStoreHead(backend, windowSize: 10);

        await head.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(95, head.Current);
    }

    [Fact]
    public async Task Refresh_CrossesAPermanentGapWiderThanTheWindow()
    {
        // 1..5 committed, 6..99 permanently missing (rolled-back sequence values), 100.. committed.
        var positions = Enumerable.Range(1, 5).Concat(Enumerable.Range(100, 20)).Select(i => (long)i).ToList();
        var head = new EventStoreHead(new RowCountBackend(positions), windowSize: 10);

        await head.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(119, head.Current);
    }
}
