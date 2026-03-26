using Alberto.Dcb;

namespace Alberto.Orders.Core.Order;

/// <summary>
/// Decider for order operations. Contains business logic as pure functions.
/// </summary>
public sealed partial class OrderDecider
{
    /// <summary>
    /// Gets the DCB query for an order's consistency boundary.
    /// </summary>
    public static DcbQuery BoundaryFor(Guid orderId) =>
        DcbQuery.For(Tags.Order, orderId);
}

/// <summary>
/// Result of a decision operation.
/// </summary>
public readonly struct DecisionResult
{
    public bool IsSuccess { get; }
    public IEvent? Event { get; }
    public string? Error { get; }

    private DecisionResult(bool isSuccess, IEvent? @event, string? error)
    {
        IsSuccess = isSuccess;
        Event = @event;
        Error = error;
    }

    public static DecisionResult Ok(IEvent @event) => new(true, @event, null);
    public static DecisionResult Fail(string error) => new(false, null, error);

    public void EnsureSuccess()
    {
        if (!IsSuccess)
            throw new InvalidOperationException(Error);
    }
}
