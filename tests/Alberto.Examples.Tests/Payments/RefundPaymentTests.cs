using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class RefundPaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c004-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c004-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    /// <summary>A payment captured for less than it was initiated for.</summary>
    private static RefundPaymentState CapturedPartially()
    {
        var evolver = new RefundPaymentEvolver();
        var state = evolver.Apply(
            new RefundPaymentState(),
            new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"));
        state = evolver.Apply(state, new PaymentAuthorized(PaymentId, "AUTH-1", Now));

        return evolver.Apply(state, new PaymentCaptured(PaymentId, 20m, Now));
    }

    [Fact]
    public void Refunds_up_to_the_captured_amount()
    {
        var decision = RefundPaymentDecider.Decide(CapturedPartially(), 20m, "Returned", Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentRefunded>()
            .Which.RefundedAmount.Should().Be(20m);
    }

    [Fact]
    public void Refuses_more_than_was_captured_even_when_more_was_initiated()
    {
        var decision = RefundPaymentDecider.Decide(CapturedPartially(), 30m, "Returned", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.amount-out-of-range");
    }

    [Fact]
    public void Refuses_a_payment_that_was_never_captured()
    {
        var evolver = new RefundPaymentEvolver();
        var state = evolver.Apply(
            new RefundPaymentState(),
            new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"));
        state = evolver.Apply(state, new PaymentAuthorized(PaymentId, "AUTH-1", Now));

        var decision = RefundPaymentDecider.Decide(state, 20m, "Returned", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.invalid-status");
    }
}
