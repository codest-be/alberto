using Alberto.Payments.Contracts;
using Alberto.Payments.Features;

namespace Alberto.Examples.Tests.Payments;

public sealed class RefundPaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c004-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c004-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static IEvent[] Authorized() =>
    [
        new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"),
        new PaymentAuthorized(PaymentId, "AUTH-1", Now)
    ];

    /// <summary>A payment captured for less than it was initiated for.</summary>
    private static IEvent[] CapturedPartially() =>
        [.. Authorized(), new PaymentCaptured(PaymentId, 20m, Now)];

    [Fact]
    public void Refunds_up_to_the_captured_amount()
    {
        Spec.For(new RefundPaymentEvolver())
            .Given(CapturedPartially())
            .When(state => RefundPaymentDecider.Decide(state, 20m, "Returned", Now))
            .ThenEmitsOnly<PaymentRefunded>(e => e.RefundedAmount == 20m);
    }

    [Fact]
    public void Refuses_more_than_was_captured_even_when_more_was_initiated()
    {
        Spec.For(new RefundPaymentEvolver())
            .Given(CapturedPartially())
            .When(state => RefundPaymentDecider.Decide(state, 30m, "Returned", Now))
            .ThenFails(PaymentProblems.AmountOutOfRange("Refund", 20m));
    }

    [Fact]
    public void Refuses_a_payment_that_was_never_captured()
    {
        Spec.For(new RefundPaymentEvolver())
            .Given(Authorized())
            .When(state => RefundPaymentDecider.Decide(state, 20m, "Returned", Now))
            .ThenFails(PaymentProblems.InvalidStatus("refunded", PaymentStatus.Authorized));
    }
}
