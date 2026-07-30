using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class FailPaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c003-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c003-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static FailPaymentState Initiated() =>
        new FailPaymentEvolver().Apply(
            new FailPaymentState(),
            new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"));

    [Fact]
    public void Fails_an_initiated_payment()
    {
        var decision = FailPaymentDecider.Decide(Initiated(), "card_declined", "Declined");

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentFailed>()
            .Which.ErrorCode.Should().Be("card_declined");
    }

    [Fact]
    public void Fails_an_authorized_payment()
    {
        var state = new FailPaymentEvolver().Apply(
            Initiated(), new PaymentAuthorized(PaymentId, "AUTH-1", Now));

        FailPaymentDecider.Decide(state, "card_declined", "Declined")
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Requires_an_error_code()
    {
        var decision = FailPaymentDecider.Decide(Initiated(), "  ", "Declined");

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.error-code-required");
    }

    [Fact]
    public void Refuses_a_captured_payment()
    {
        var evolver = new FailPaymentEvolver();
        var state = evolver.Apply(Initiated(), new PaymentAuthorized(PaymentId, "AUTH-1", Now));
        state = evolver.Apply(state, new PaymentCaptured(PaymentId, 49.95m, Now));

        var decision = FailPaymentDecider.Decide(state, "card_declined", "Declined");

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Message.Should().Contain("Captured");
    }
}
