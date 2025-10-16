namespace Alberto.Example.Modules.Payments.Api.Contracts;

public record PaymentDto(
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string Status);