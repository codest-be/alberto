using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class GetPaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c005-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c005-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset At = new(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Folds_the_whole_payment_history()
    {
        var evolver = new GetPaymentEvolver();
        var state = new GetPaymentState();

        state = evolver.Apply(state, new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"));
        state = evolver.Apply(state, new PaymentAuthorized(PaymentId, "AUTH-1", At));
        state = evolver.Apply(state, new PaymentCaptured(PaymentId, 49.95m, At.AddHours(1)));
        state = evolver.Apply(state, new PaymentRefunded(PaymentId, 20m, "Damaged", At.AddHours(2)));

        state.Exists.Should().BeTrue();
        state.PaymentId.Should().Be(PaymentId);
        state.OrderId.Should().Be(OrderId);
        state.Amount.Should().Be(49.95m);
        state.Currency.Should().Be("EUR");
        state.PaymentMethod.Should().Be("card");
        state.Status.Should().Be(PaymentStatus.Refunded);
        state.AuthorizationCode.Should().Be("AUTH-1");
        state.AuthorizedAt.Should().Be(At);
        state.CapturedAt.Should().Be(At.AddHours(1));
        state.RefundedAmount.Should().Be(20m);
        state.RefundedAt.Should().Be(At.AddHours(2));
    }

    [Fact]
    public void Carries_the_failure_reason()
    {
        var evolver = new GetPaymentEvolver();
        var state = evolver.Apply(
            new GetPaymentState(),
            new PaymentInitiated(PaymentId, OrderId, 49.95m, "EUR", "card"));

        state = evolver.Apply(state, new PaymentFailed(PaymentId, "card_declined", "Declined by issuer"));

        state.Status.Should().Be(PaymentStatus.Failed);
        state.ErrorCode.Should().Be("card_declined");
        state.ErrorMessage.Should().Be("Declined by issuer");
    }

    [Fact]
    public void A_payment_with_no_events_does_not_exist()
    {
        new GetPaymentState().Exists.Should().BeFalse();
    }

    [Fact]
    public void Handles_every_payment_event_so_a_new_one_cannot_be_silently_dropped()
    {
        new GetPaymentEvolver().HandledEventTypes.Should().BeEquivalentTo(
            ["payment-initiated", "payment-authorized", "payment-captured", "payment-failed",
             "payment-refunded"]);
    }
}
