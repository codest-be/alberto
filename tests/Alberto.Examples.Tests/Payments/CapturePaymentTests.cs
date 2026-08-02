using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class CapturePaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c002-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c002-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static readonly PaymentInitiated Initiated =
        new(PaymentId, OrderId, 49.95m, "EUR", "card");

    private static IEvent[] Authorized() =>
        [Initiated, new PaymentAuthorized(PaymentId, "AUTH-1", Now)];

    [Fact]
    public void Captures_the_initiated_amount_when_none_is_given()
    {
        Spec.For(new CapturePaymentEvolver())
            .Given(Authorized())
            .When(state => CapturePaymentDecider.Decide(state, null, Now))
            .ThenEmitsOnly<PaymentCaptured>(e => e.CapturedAmount == 49.95m);
    }

    [Fact]
    public void Captures_a_partial_amount()
    {
        Spec.For(new CapturePaymentEvolver())
            .Given(Authorized())
            .When(state => CapturePaymentDecider.Decide(state, 20m, Now))
            .ThenEmitsOnly<PaymentCaptured>(e => e.CapturedAmount == 20m);
    }

    [Fact]
    public void Refuses_more_than_the_initiated_amount()
    {
        Spec.For(new CapturePaymentEvolver())
            .Given(Authorized())
            .When(state => CapturePaymentDecider.Decide(state, 50m, Now))
            .ThenFails(PaymentProblems.AmountOutOfRange("Capture", 49.95m));
    }

    [Fact]
    public void Refuses_a_payment_that_was_never_authorized()
    {
        Spec.For(new CapturePaymentEvolver())
            .Given(Initiated)
            .When(state => CapturePaymentDecider.Decide(state, null, Now))
            .ThenFails(PaymentProblems.InvalidStatus("captured", PaymentStatus.Initiated));
    }
}
