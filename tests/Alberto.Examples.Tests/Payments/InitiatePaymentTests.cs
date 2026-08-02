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
        Spec.For(new InitiatePaymentEvolver())
            .GivenNoEvents()
            .When(state => InitiatePaymentDecider.Decide(state, PaymentId, OrderId, 49.95m, "EUR", "card"))
            .ThenEmitsOnly<PaymentInitiated>(e => e.Amount == 49.95m);
    }

    [Fact]
    public void Refuses_a_second_initiation_for_the_same_id()
    {
        Spec.For(new InitiatePaymentEvolver())
            .Given(new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"))
            .When(state => InitiatePaymentDecider.Decide(state, PaymentId, OrderId, 49.95m, "EUR", "card"))
            .ThenFails(PaymentProblems.AlreadyExists(PaymentId));
    }

    [Fact]
    public void Requires_a_positive_amount()
    {
        Spec.For(new InitiatePaymentEvolver())
            .GivenNoEvents()
            .When(state => InitiatePaymentDecider.Decide(state, PaymentId, OrderId, 0m, "EUR", "card"))
            .ThenFails(PaymentProblems.InvalidAmount());
    }

    [Fact]
    public void Handles_only_the_event_that_decides_existence()
    {
        new InitiatePaymentEvolver().HandledEventTypes.Should().BeEquivalentTo(["payment-initiated"]);
    }
}
