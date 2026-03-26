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
    public static DecisionResult<IEvent> Initiate(
        IInitiatePaymentState state,
        Guid paymentId,
        Guid orderId,
        decimal amount,
        string currency,
        string paymentMethod)
    {
        if (state.Exists)
            return DecisionResult<IEvent>.Failure($"Payment {paymentId} already exists");

        if (orderId == Guid.Empty)
            return DecisionResult<IEvent>.Failure("Order ID is required");

        if (amount <= 0)
            return DecisionResult<IEvent>.Failure("Amount must be greater than zero");

        if (string.IsNullOrWhiteSpace(currency))
            return DecisionResult<IEvent>.Failure("Currency is required");

        if (string.IsNullOrWhiteSpace(paymentMethod))
            return DecisionResult<IEvent>.Failure("Payment method is required");

        return DecisionResult<IEvent>.Success(new PaymentInitiated(paymentId, orderId, amount, currency, paymentMethod));
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
