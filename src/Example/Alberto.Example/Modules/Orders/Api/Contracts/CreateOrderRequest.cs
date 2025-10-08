namespace Alberto.Example.Modules.Orders.Api.Contracts;

public record CreateOrderRequest(decimal Amount, string CustomerId);