namespace Alberto.Projections;

/// <summary>
/// Repository for storing and retrieving projected read models.
/// Provides a storage abstraction with pluggable backends (in-memory, Postgres, etc.).
/// </summary>
/// <typeparam name="TKey">The type of the key used to identify projections</typeparam>
/// <typeparam name="TState">The projected state type</typeparam>
public interface IProjectionRepository<in TKey, TState>
    where TKey : notnull
    where TState : new()
{
    /// <summary>
    /// Retrieves a projection by its key.
    /// </summary>
    /// <param name="key">The key identifying the projection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The projection state, or null if not found</returns>
    Task<TState?> Get(TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all projections.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All projection states</returns>
    Task<IReadOnlyCollection<TState>> GetAll(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates a projection.
    /// </summary>
    /// <param name="key">The key identifying the projection</param>
    /// <param name="state">The projection state to store</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task Upsert(TKey key, TState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing projection using an update function.
    /// If the projection doesn't exist, creates a new one with the result of applying the update function to a new state.
    /// </summary>
    /// <param name="key">The key identifying the projection</param>
    /// <param name="updateFn">Function to update the existing state</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task Update(TKey key, Func<TState, TState> updateFn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a projection by its key.
    /// </summary>
    /// <param name="key">The key identifying the projection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task Delete(TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a projection exists.
    /// </summary>
    /// <param name="key">The key identifying the projection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the projection exists, false otherwise</returns>
    Task<bool> Exists(TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all projections.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task Clear(CancellationToken cancellationToken = default);
}