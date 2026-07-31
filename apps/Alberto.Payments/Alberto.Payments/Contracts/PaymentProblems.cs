using Alberto;

namespace Alberto.Payments.Contracts;

/// <summary>
/// The failure taxonomy for payment decisions. Codes follow <c>payment.{kebab-reason}</c>
/// and are the contract the API layer maps to typed errors — callers match on
/// <see cref="Problem.Code"/>, never on message text.
/// </summary>
public static class PaymentProblems
{
    public static Problem NotFound() =>
        Problem.Create("payment.not-found", "Payment does not exist");

    public static Problem AlreadyExists(Guid paymentId) =>
        Problem.Create(
            "payment.already-exists",
            $"Payment {paymentId} already exists",
            new Dictionary<string, object> { ["paymentId"] = paymentId });

    /// <summary>
    /// The payment is in a status that does not permit <paramref name="operation"/>.
    /// One code, with the attempted operation and blocking status in the details.
    /// </summary>
    public static Problem InvalidStatus(string operation, PaymentStatus status) =>
        Problem.Create(
            "payment.invalid-status",
            $"Payment cannot be {operation} in {status} status",
            new Dictionary<string, object>
            {
                ["operation"] = operation,
                ["status"] = status.ToString()
            });

    public static Problem AmountOutOfRange(string operation, decimal maximum) =>
        Problem.Create(
            "payment.amount-out-of-range",
            $"{operation} amount must be between 0 and {maximum}",
            new Dictionary<string, object>
            {
                ["operation"] = operation,
                ["maximum"] = maximum
            });

    public static Problem InvalidAmount() =>
        Problem.Create("payment.invalid-amount", "Amount must be greater than zero");

    public static Problem OrderRequired() =>
        Problem.Create("payment.order-required", "Order ID is required");

    public static Problem CurrencyRequired() =>
        Problem.Create("payment.currency-required", "Currency is required");

    public static Problem PaymentMethodRequired() =>
        Problem.Create("payment.method-required", "Payment method is required");

    public static Problem AuthorizationCodeRequired() =>
        Problem.Create("payment.authorization-code-required", "Authorization code is required");

    public static Problem ErrorCodeRequired() =>
        Problem.Create("payment.error-code-required", "Error code is required");
}
