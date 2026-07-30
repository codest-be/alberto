using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class PaymentSummariesTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c006-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c006-0000-7000-8000-000000000002");

    [Theory]
    [InlineData(PaymentSummaryStatus.Initiated, PaymentStatus.Initiated)]
    [InlineData(PaymentSummaryStatus.Authorized, PaymentStatus.Authorized)]
    [InlineData(PaymentSummaryStatus.Captured, PaymentStatus.Captured)]
    [InlineData(PaymentSummaryStatus.Failed, PaymentStatus.Failed)]
    [InlineData(PaymentSummaryStatus.Refunded, PaymentStatus.Refunded)]
    public void Maps_read_model_status_by_name_not_by_ordinal(
        PaymentSummaryStatus stored, PaymentStatus expected)
    {
        // The two enums do not share ordinals: the contract one starts at None = 0, the read
        // model's at Initiated = 0. A numeric cast shifts every payment one rung down.
        var payment = Payment.FromSummary(Summary(stored));

        payment.Status.Should().Be(expected);
    }

    [Fact]
    public void Maps_every_summary_field_onto_the_graphql_type()
    {
        var summary = Summary(PaymentSummaryStatus.Refunded) with
        {
            AuthorizationCode = "AUTH-1",
            ErrorCode = "card_declined",
            ErrorMessage = "Declined by issuer",
            RefundedAmount = 12.50m,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            AuthorizedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            CapturedAt = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero),
            RefundedAt = new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero),
        };

        var payment = Payment.FromSummary(summary);

        payment.PaymentId.Should().Be(PaymentId);
        payment.OrderId.Should().Be(OrderId);
        payment.Amount.Should().Be(49.95m);
        payment.Currency.Should().Be("EUR");
        payment.PaymentMethod.Should().Be("card");
        payment.AuthorizationCode.Should().Be("AUTH-1");
        payment.ErrorCode.Should().Be("card_declined");
        payment.ErrorMessage.Should().Be("Declined by issuer");
        payment.RefundedAmount.Should().Be(12.50m);
        payment.CreatedAt.Should().Be(summary.CreatedAt);
        payment.AuthorizedAt.Should().Be(summary.AuthorizedAt);
        payment.CapturedAt.Should().Be(summary.CapturedAt);
        payment.RefundedAt.Should().Be(summary.RefundedAt);
    }

    [Fact]
    public void Declaration_handles_every_payment_event()
    {
        PaymentSummaryProjection.Declaration.HandledEventTypes.Should().BeEquivalentTo(
            ["payment-initiated", "payment-authorized", "payment-captured", "payment-failed",
             "payment-refunded"]);
    }

    [Fact]
    public void Declaration_keeps_the_processor_id_its_checkpoint_and_state_rows_are_keyed_by()
    {
        PaymentSummaryProjection.Declaration.ProcessorId
            .Should().Be(nameof(PaymentSummaryProjection));
    }

    private static PaymentSummary Summary(PaymentSummaryStatus status) => new()
    {
        PaymentId = PaymentId,
        OrderId = OrderId,
        Amount = 49.95m,
        Currency = "EUR",
        PaymentMethod = "card",
        Status = status,
    };
}
