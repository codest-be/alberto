using Alberto.Payments.Core.Events;

namespace Alberto.Payments.Core.Payment.Actions;

public interface IInitiatePaymentState
{
    bool Exists { get; }
}

public sealed partial class PaymentDecider
{
    /// <summary>
    /// Initiates a new payment.
    /// </summary>
    public static PaymentDecisionResult Initiate(
        IInitiatePaymentState state,
        Guid paymentId,
        Guid orderId,
        decimal amount,
        string currency,
        string paymentMethod)
    {
        if (state.Exists)
            return PaymentDecisionResult.Fail($"Payment {paymentId} already exists");

        if (orderId == Guid.Empty)
            return PaymentDecisionResult.Fail("Order ID is required");

        if (amount <= 0)
            return PaymentDecisionResult.Fail("Amount must be greater than zero");

        if (string.IsNullOrWhiteSpace(currency))
            return PaymentDecisionResult.Fail("Currency is required");

        if (string.IsNullOrWhiteSpace(paymentMethod))
            return PaymentDecisionResult.Fail("Payment method is required");

        return PaymentDecisionResult.Ok(new PaymentInitiated(paymentId, orderId, amount, currency, paymentMethod));
    }

    public PaymentState Apply(PaymentState state, PaymentInitiated e) => state with
    {
        PaymentId = e.PaymentId,
        OrderId = e.OrderId,
        Amount = e.Amount,
        Currency = e.Currency,
        PaymentMethod = e.PaymentMethod,
        Status = PaymentStatus.Initiated
    };
}
