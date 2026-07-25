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
            return Decision.Fail(PaymentProblems.AlreadyExists(paymentId));

        if (orderId == Guid.Empty)
            return Decision.Fail(PaymentProblems.OrderRequired());

        if (amount <= 0)
            return Decision.Fail(PaymentProblems.InvalidAmount());

        if (string.IsNullOrWhiteSpace(currency))
            return Decision.Fail(PaymentProblems.CurrencyRequired());

        if (string.IsNullOrWhiteSpace(paymentMethod))
            return Decision.Fail(PaymentProblems.PaymentMethodRequired());

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
