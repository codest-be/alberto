namespace Alberto.EventStore.Subscriptions.Batching;

/// <summary>
/// Ambient marker for batching mode.
/// When active, projection handlers accumulate updates in memory instead of saving immediately.
/// External code is responsible for collecting and committing batched updates.
/// </summary>
public sealed class ProjectionBatchScope : IAsyncDisposable
{
    // ReSharper disable once InconsistentNaming
    private static readonly AsyncLocal<ProjectionBatchScope?> _current = new();
    private readonly Dictionary<object, object> _accumulators = new();
    private readonly List<Func<CancellationToken, ValueTask>> _commits = new();
    private bool _committed;
    private bool _disposed;

    public ProjectionBatchScope()
    {
        _current.Value = this;
    }

    /// <summary>
    /// Gets the current ambient batch scope, if any
    /// </summary>
    public static ProjectionBatchScope? Current => _current.Value;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            // Auto-commit on dispose if not already committed
            if (!_committed)
            {
                await CommitAll();
            }
        }
        finally
        {
            _disposed = true;

            // Clear ambient context
            if (_current.Value == this)
            {
                _current.Value = null;
            }
        }
    }

    /// <summary>
    /// Gets or creates an accumulator for the given key
    /// </summary>
    public TAccumulator GetOrCreateAccumulator<TAccumulator>(object key, Func<TAccumulator> factory)
        where TAccumulator : class
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProjectionBatchScope));

        if (_accumulators.TryGetValue(key, out var existing))
        {
            return (TAccumulator)existing;
        }

        var accumulator = factory();
        _accumulators[key] = accumulator;
        return accumulator;
    }

    /// <summary>
    /// Registers a commit action to be executed when the scope completes
    /// </summary>
    public void RegisterCommit(Func<CancellationToken, ValueTask> commitAction)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProjectionBatchScope));

        _commits.Add(commitAction);
    }

    /// <summary>
    /// Commits all registered actions
    /// </summary>
    public async ValueTask CommitAll(CancellationToken cancellationToken = default)
    {
        if (_committed)
            return;

        if (_disposed)
            throw new ObjectDisposedException(nameof(ProjectionBatchScope));

        // Execute all commits
        foreach (var commit in _commits)
        {
            await commit(cancellationToken);
        }

        _committed = true;
    }
}