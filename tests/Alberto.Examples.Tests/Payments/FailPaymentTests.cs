using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class FailPaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c003-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c003-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static readonly PaymentInitiated Initiated =
        new(PaymentId, OrderId, 49.95m, "EUR", "card");

    [Fact]
    public void Fails_an_initiated_payment()
    {
        Spec.For(new FailPaymentEvolver())
            .Given(Initiated)
            .When(state => FailPaymentDecider.Decide(state, "card_declined", "Declined"))
            .ThenEmitsOnly<PaymentFailed>(e => e.ErrorCode == "card_declined");
    }

    [Fact]
    public void Fails_an_authorized_payment()
    {
        Spec.For(new FailPaymentEvolver())
            .Given(Initiated, new PaymentAuthorized(PaymentId, "AUTH-1", Now))
            .When(state => FailPaymentDecider.Decide(state, "card_declined", "Declined"))
            .ThenEmitsOnly<PaymentFailed>();
    }

    [Fact]
    public void Requires_an_error_code()
    {
        Spec.For(new FailPaymentEvolver())
            .Given(Initiated)
            .When(state => FailPaymentDecider.Decide(state, "  ", "Declined"))
            .ThenFails(PaymentProblems.ErrorCodeRequired());
    }

    [Fact]
    public void Refuses_a_captured_payment()
    {
        Spec.For(new FailPaymentEvolver())
            .Given(
                Initiated,
                new PaymentAuthorized(PaymentId, "AUTH-1", Now),
                new PaymentCaptured(PaymentId, 49.95m, Now))
            .When(state => FailPaymentDecider.Decide(state, "card_declined", "Declined"))
            .ThenFails("payment.invalid-status")
            .ThenProblems(problems => problems.Single().Message.Should().Contain("Captured"));
    }
}
