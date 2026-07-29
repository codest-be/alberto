using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Orders.Features;

/// <summary>Input for creating an order.</summary>
public sealed record CreateOrderInput(
    Guid CustomerId,
    List<OrderItemInput> LineItems,
    string? Notes);

/// <summary>Input for order line items.</summary>
public sealed record OrderItemInput(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);

/// <summary>Result of a create mutation.</summary>
public readonly record struct CreateOrderResult(Guid OrderId);

/// <summary>
/// Everything creation decides on: whether the order is already there. Not the customer, not the
/// items, not the status — a second OrderCreated is refused whatever any of those say.
/// </summary>
public sealed record CreateOrderState
{
    public Guid OrderId { get; init; }

    public bool Exists => OrderId != Guid.Empty;
}

/// <summary>
/// Folds only <see cref="OrderCreated"/>. The dispatcher ignores every other event type, so the
/// six events this slice does not name cost it nothing to read past.
/// </summary>
public sealed class CreateOrderEvolver : Evolver<CreateOrderState>,
    IEvolve<CreateOrderState, OrderCreated>
{
    public CreateOrderState Apply(CreateOrderState s, OrderCreated e) => s with { OrderId = e.OrderId };
}

public static class CreateOrderDecider
{
    /// <summary>
    /// Whole-order boundary: the id is fresh, so this reads empty and the append still fails if
    /// anything claimed the order between the read and the write.
    /// </summary>
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(
        CreateOrderState state,
        Guid orderId,
        Guid customerId,
        IReadOnlyList<OrderLineItem> lineItems,
        string? notes = null)
    {
        if (state.Exists)
            return Decision.Fail(OrderProblems.AlreadyExists(orderId));

        if (customerId == Guid.Empty)
            return Decision.Fail(OrderProblems.CustomerRequired());

        return Decision.Succeed(new OrderCreated(orderId, customerId, lineItems, notes));
    }
}

public static class CreateOrderMutation
{
    /// <summary>
    /// Creates a new order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Creates a new order with the specified line items.")]
    public static async Task<CreateOrderResult> CreateOrder(
        CreateOrderInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var orderId = Guid.CreateVersion7();
        var lineItems = input.LineItems
            .Select(x => new OrderLineItem(x.ProductId, x.ProductName, x.Quantity, x.UnitPrice))
            .ToList();

        // No RetryOnConflict: a conflict here means someone else claimed this id, and re-deciding
        // would only refuse it again.
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(input)
            .Load(CreateOrderDecider.Boundary(orderId), new CreateOrderEvolver())
            .Decide((cmd, state) =>
                CreateOrderDecider.Decide(state, orderId, cmd.CustomerId, lineItems, cmd.Notes))
            .Commit(ct);

        result.EnsureCommitted();
        return new CreateOrderResult(orderId);
    }
}
