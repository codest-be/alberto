namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Result of a projection operation. Expresses intent without knowing storage details.
/// Infrastructure handles insert vs update via upsert - projections just say "Set".
/// </summary>
/// <typeparam name="TState">The type of the projection state.</typeparam>
public abstract record ProjectionResult<TState>
{
    private ProjectionResult() { }

    /// <summary>
    /// The projection doesn't care about this event - no database operation needed.
    /// </summary>
    public sealed record Unchanged : ProjectionResult<TState>;

    /// <summary>
    /// Set the state to this value. Infrastructure handles insert vs update.
    /// </summary>
    public sealed record Set(TState State) : ProjectionResult<TState>;

    /// <summary>
    /// Delete the document from storage.
    /// </summary>
    public sealed record Delete : ProjectionResult<TState>;

    /// <summary>
    /// Implicit conversion from TState to Set(TState).
    /// This makes the common case clean:
    /// <code>
    /// return new OrderSummary { ... };  // Implicitly becomes Set(state)
    /// </code>
    /// </summary>
    public static implicit operator ProjectionResult<TState>(TState state) => new Set(state);
}

/// <summary>
/// Factory methods for creating projection results.
/// Use these when you need explicit control (Delete, Unchanged).
/// For Set, just return the state directly - implicit conversion handles it.
/// </summary>
public static class Projection
{
    /// <summary>
    /// Signal that the projection doesn't care about this event.
    /// </summary>
    public static ProjectionResult<TState> Unchanged<TState>() => new ProjectionResult<TState>.Unchanged();

    /// <summary>
    /// Set the state explicitly. Usually you can just return the state directly.
    /// </summary>
    public static ProjectionResult<TState> Set<TState>(TState state) => new ProjectionResult<TState>.Set(state);

    /// <summary>
    /// Delete the document from storage.
    /// </summary>
    public static ProjectionResult<TState> Delete<TState>() => new ProjectionResult<TState>.Delete();
}
