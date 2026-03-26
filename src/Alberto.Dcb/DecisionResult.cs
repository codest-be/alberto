namespace Alberto.Dcb;

/// <summary>
/// Result of a decision — either events to append or a failure reason.
/// </summary>
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
