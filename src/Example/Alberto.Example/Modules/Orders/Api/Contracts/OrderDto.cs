namespace Alberto.Example.Modules.Orders.Api.Contracts;

public sealed record OrderDto(
    Guid OrderId,
    decimal Amount,
    string CustomerId,
    string Status,
    string? TrackingNumber,
    string? CancellationReason);