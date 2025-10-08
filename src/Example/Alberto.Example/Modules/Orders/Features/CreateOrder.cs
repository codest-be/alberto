using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.CQRS.Validation;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.EventHandlers;

namespace Alberto.Example.Modules.Orders.Features;

public static class CreateOrderEndpoint
{
    public static IEndpointRouteBuilder MapCreateOrder(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders", async (
                CreateOrderRequest request,
                ICommandHandler<CreateOrderCommand, Guid> handler,
                CancellationToken ct) =>
            {
                var command = new CreateOrderCommand(request.Amount, request.CustomerId);
                var result = await handler.Handle(command, ct);

                return result.ToHttpResult();
            })
            .WithName("CreateOrder");

        return endpoints;
    }
}

public sealed record CreateOrderCommand(decimal Amount, string CustomerId) : ICommand;

public sealed class CreateOrderValidator : IValidator<CreateOrderCommand>
{
    public Result Validate(CreateOrderCommand command)
    {
        if (command.Amount <= 0)
            return Result.Fail(Problem.Create("INVALID_AMOUNT", "Order amount must be greater than zero"));

        if (string.IsNullOrWhiteSpace(command.CustomerId))
            return Result.Fail(Problem.Create("INVALID_CUSTOMER", "Customer ID is required"));

        return Result.Success();
    }
}

internal sealed record CreateOrderState
{
    public bool Exists { get; init; }
}

internal sealed class CreateOrderProjector : IProjector<CreateOrderState>
{
    public CreateOrderState Apply(CreateOrderState state, object @event)
    {
        return @event switch
        {
            OrderCreated => state with { Exists = true },
            _ => state
        };
    }
}

internal static class CreateOrderDecider
{
    public static Decision<Guid> Decide(CreateOrderState state, CreateOrderCommand command)
    {
        if (state.Exists)
            return Decision<Guid>.Fail(Problem.Create("ORDER_ALREADY_EXISTS", "Order already exists"));

        var orderId = Guid.CreateVersion7();
        var orderCreated = new OrderCreated(orderId, command.Amount, command.CustomerId);

        return Decision<Guid>.Succeed(orderId, orderCreated);
    }
}

public sealed class CreateOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<CreateOrderCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateOrderCommand command, CancellationToken cancellationToken = default)
    {
        var tempOrderId = Guid.CreateVersion7();

        // Query only order-created events to check existence
        var query = new StreamQuery([new EventTag(Tags.Order, tempOrderId.ToString())])
            .WithEventType<OrderCreated>();

        var projector = new CreateOrderProjector();
        var (events, lastEventId) = await eventStore.Load(query, cancellationToken);
        var state = projector.Evolve(events);

        var decision = CreateOrderDecider.Decide(state, command);

        if (decision.IsError)
            return Result<Guid>.Fail(decision.Problems.First());

        await eventStore.PersistNew(query, decision.Events, cancellationToken);

        return Result<Guid>.Success(decision.Value);
    }
}