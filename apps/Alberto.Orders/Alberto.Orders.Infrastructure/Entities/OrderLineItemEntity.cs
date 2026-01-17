namespace Alberto.Orders.Infrastructure.Entities;

/// <summary>
/// Line item data stored as JSON within OrderSummaryEntity.
/// Not a separate database table - embedded in parent entity.
/// </summary>
public sealed class OrderLineItemData
{
    /// <summary>
    /// The product identifier.
    /// </summary>
    public Guid ProductId { get; set; }

    /// <summary>
    /// Name of the product.
    /// </summary>
    public string ProductName { get; set; } = "";

    /// <summary>
    /// Quantity ordered.
    /// </summary>
    public int Quantity { get; set; }

    /// <summary>
    /// Price per unit.
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Total for this line item.
    /// </summary>
    public decimal Total { get; set; }
}
