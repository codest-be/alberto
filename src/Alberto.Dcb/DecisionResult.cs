namespace Alberto.Dcb;

/// <summary>
/// Result of a decision — either events to append or a failure reason.
/// </summary>
/// <remarks>
/// <b>Obsolete:</b> Use <c>Decision</c> or <c>Decision&lt;T&gt;</c> (from Alberto.Dcb.Commands) together with
/// <see cref="Result"/> / <see cref="Result{T}"/> instead.
/// <c>Decision</c> carries both the success/failure signal and the events to append.
/// <see cref="Result"/> / <see cref="Result{T}"/> are returned by the persist step after events
/// have been written. This type will be removed in a future version.
/// </remarks>
[Obsolete(
    "Use Decision or Decision<T> instead of DecisionResult<TEvent>. " +
    "Decision carries events and problems; Result/Result<T> are returned after persisting. " +
    "This type will be removed in a future version.",
    error: false)]
public abstract record DecisionResult<TEvent> where TEvent : IEvent
{
    private DecisionResult() { }

    /// <summary>Successful decision with events to append.</summary>
    public sealed record Ok(IReadOnlyList<TEvent> Events) : DecisionResult<TEvent>;

    /// <summary>Failed decision with a reason.</summary>
    public sealed record Fail(string Reason) : DecisionResult<TEvent>;

    /// <summary>Creates a successful result with the given events.</summary>
    public static DecisionResult<TEvent> Success(params TEvent[] events)
        => new Ok(events);

    /// <summary>Creates a failure result with the given reason.</summary>
    public static DecisionResult<TEvent> Failure(string reason)
        => new Fail(reason);

    /// <summary>Returns true if this is a successful result.</summary>
    public bool IsSuccess => this is Ok;

    /// <summary>Returns true if this is a failure result.</summary>
    public bool IsFailure => this is Fail;

    /// <summary>
    /// Returns events if successful, throws <see cref="InvalidOperationException"/> if failed.
    /// </summary>
    public IReadOnlyList<TEvent> EnsureSuccess()
        => this is Ok ok ? ok.Events
            : throw new InvalidOperationException(((Fail)this).Reason);
}
