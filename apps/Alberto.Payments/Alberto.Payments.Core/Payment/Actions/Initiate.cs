using Alberto.Dcb;
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
    public static Decision Initiate(
        IInitiatePaymentState state,
        Guid paymentId,
        Guid orderId,
        decimal amount,
        string currency,
        string paymentMethod)
    {
        if (state.Exists)
            return Problem.Create("payment-already-exists", $"Payment {paymentId} already exists");

        if (orderId == Guid.Empty)
            return Problem.Create("order-id-required", "Order ID is required");

        if (amount <= 0)
            return Problem.Create("invalid-amount", "Amount must be greater than zero");

        if (string.IsNullOrWhiteSpace(currency))
            return Problem.Create("currency-required", "Currency is required");

        if (string.IsNullOrWhiteSpace(paymentMethod))
            return Problem.Create("payment-method-required", "Payment method is required");

        return Decision.Succeed(new PaymentInitiated(paymentId, orderId, amount, currency, paymentMethod));
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
