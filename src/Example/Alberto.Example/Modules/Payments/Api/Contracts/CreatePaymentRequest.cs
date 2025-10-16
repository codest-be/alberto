namespace Alberto.Example.Modules.Payments.Api.Contracts;

public record CreatePaymentRequest(Guid OrderId, decimal Amount);