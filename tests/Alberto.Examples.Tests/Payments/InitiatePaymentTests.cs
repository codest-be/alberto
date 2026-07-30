using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class InitiatePaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c000-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c000-0000-7000-8000-000000000002");

    [Fact]
    public void Initiates_a_payment()
    {
        var decision = InitiatePaymentDecider.Decide(
            new InitiatePaymentState(), PaymentId, OrderId, 49.95m, "EUR", "card");

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentInitiated>()
            .Which.Amount.Should().Be(49.95m);
    }

    [Fact]
    public void Refuses_a_second_initiation_for_the_same_id()
    {
        var state = new InitiatePaymentEvolver().Apply(
            new InitiatePaymentState(),
            new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"));

        var decision = InitiatePaymentDecider.Decide(
            state, PaymentId, OrderId, 49.95m, "EUR", "card");

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.already-exists");
    }

    [Fact]
    public void Requires_a_positive_amount()
    {
        var decision = InitiatePaymentDecider.Decide(
            new InitiatePaymentState(), PaymentId, OrderId, 0m, "EUR", "card");

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.invalid-amount");
    }

    [Fact]
    public void Handles_only_the_event_that_decides_existence()
    {
        new InitiatePaymentEvolver().HandledEventTypes.Should().BeEquivalentTo(["payment-initiated"]);
    }
}
