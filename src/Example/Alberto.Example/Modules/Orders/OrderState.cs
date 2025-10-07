namespace Alberto.Example.Modules.Orders;

/// <summary>
/// Aggregate state for an Order, reconstructed from events
/// </summary>
public sealed record OrderState
{
    public Guid OrderId { get; set; } = Guid.Empty;
    public decimal Amount { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public OrderStatus Status { get; set; } = OrderStatus.Draft;
    public string? TrackingNumber { get; set; }
    public string? CancellationReason { get; set; }
}

public enum OrderStatus
{
    Draft,
    Created,
    Placed,
    Shipped,
    Cancelled
}