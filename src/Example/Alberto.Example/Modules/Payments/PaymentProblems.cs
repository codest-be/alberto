using Alberto.EventSourcing;
using Alberto.Example.Modules.Payments.Enums;

namespace Alberto.Example.Modules.Payments;

/// <summary>
/// Centralized problem definitions for payment operations
/// </summary>
public static class PaymentProblems
{
    public static Problem PaymentNotFound(Guid paymentId) =>
        Problem.Create("PAYMENT_NOT_FOUND", $"Payment {paymentId} does not exist");

    public static Problem PaymentAlreadyProcessed() =>
        Problem.Create("PAYMENT_ALREADY_PROCESSED", "Payment has already been processed");

    public static Problem InvalidStatusForProcessing(PaymentStatus currentStatus) =>
        Problem.Create("INVALID_PAYMENT_STATUS",
            $"Payment must be in Created status to be processed. Current status: {currentStatus}");
}