using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class CapturePaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c002-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c002-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static CapturePaymentState Authorized()
    {
        var evolver = new CapturePaymentEvolver();
        var state = evolver.Apply(
            new CapturePaymentState(),
            new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"));

        return evolver.Apply(state, new PaymentAuthorized(PaymentId, "AUTH-1", Now));
    }

    [Fact]
    public void Captures_the_initiated_amount_when_none_is_given()
    {
        var decision = CapturePaymentDecider.Decide(Authorized(), null, Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentCaptured>()
            .Which.CapturedAmount.Should().Be(49.95m);
    }

    [Fact]
    public void Captures_a_partial_amount()
    {
        var decision = CapturePaymentDecider.Decide(Authorized(), 20m, Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentCaptured>()
            .Which.CapturedAmount.Should().Be(20m);
    }

    [Fact]
    public void Refuses_more_than_the_initiated_amount()
    {
        var decision = CapturePaymentDecider.Decide(Authorized(), 50m, Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.amount-out-of-range");
    }

    [Fact]
    public void Refuses_a_payment_that_was_never_authorized()
    {
        var state = new CapturePaymentEvolver().Apply(
            new CapturePaymentState(),
            new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"));

        var decision = CapturePaymentDecider.Decide(state, null, Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.invalid-status");
    }
}
