using Alberto.Orders.Core;

namespace Alberto.Orders.Infrastructure.ReadModels;

/// <summary>
/// Aggregate read model for orders overview dashboard.
/// Single document per tenant tracking order statistics.
/// </summary>
public sealed record OrdersOverview
{
    public int TotalOrders { get; init; }
    public int DraftOrders { get; init; }
    public int ConfirmedOrders { get; init; }
    public int ShippedOrders { get; init; }
    public int DeliveredOrders { get; init; }
    public int CancelledOrders { get; init; }
    public decimal TotalRevenue { get; init; }
    public decimal AverageOrderValue { get; init; }
    public DateTimeOffset? LastOrderAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
