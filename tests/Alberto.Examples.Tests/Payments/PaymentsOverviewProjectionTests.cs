using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

/// <summary>
/// The payments aggregate folded on its own: every event lands on one document.
/// </summary>
public sealed class PaymentsOverviewProjectionTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c006-0000-7000-8000-000000000001");
    private static readonly Guid OtherPaymentId = Guid.Parse("0197c006-0000-7000-8000-000000000011");
    private static readonly Guid OrderId = Guid.Parse("0197c006-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static PaymentInitiated Initiated(Guid paymentId) =>
        new(paymentId, OrderId, 49.95m, "EUR", "card");

    [Fact]
    public void Counts_every_payment_that_is_initiated()
    {
        ProjectionSpec.For(PaymentsOverviewProjection.Declaration)
            .GivenNoEvents()
            .When(Initiated(PaymentId), Initiated(OtherPaymentId))
            .ThenDocument(PaymentsOverviewProjection.DocumentId, overview =>
                overview.TotalPayments.Should().Be(2));
    }

    [Fact]
    public void Sums_what_was_captured_rather_than_what_was_asked_for()
    {
        // A partial capture is captured, not initiated: the amount that counts is the one on the
        // capture event.
        ProjectionSpec.For(PaymentsOverviewProjection.Declaration)
            .Given(Initiated(PaymentId), new PaymentAuthorized(PaymentId, "AUTH-1", Now))
            .When(new PaymentCaptured(PaymentId, 30.00m, Now))
            .ThenDocument(overview =>
            {
                overview.CapturedPayments.Should().Be(1);
                overview.TotalCapturedAmount.Should().Be(30.00m);
                overview.AuthorizedPayments.Should().Be(1);
            });
    }

    [Fact]
    public void Tracks_refunds_separately_from_captures_rather_than_netting_them_off()
    {
        ProjectionSpec.For(PaymentsOverviewProjection.Declaration)
            .Given(
                Initiated(PaymentId),
                new PaymentAuthorized(PaymentId, "AUTH-1", Now),
                new PaymentCaptured(PaymentId, 49.95m, Now))
            .When(new PaymentRefunded(PaymentId, 12.50m, "damaged", Now))
            .ThenDocument(overview =>
            {
                overview.TotalCapturedAmount.Should().Be(49.95m);
                overview.RefundedPayments.Should().Be(1);
                overview.TotalRefundedAmount.Should().Be(12.50m);
            });
    }

    [Fact]
    public void Counts_a_failure_without_disturbing_the_amounts()
    {
        ProjectionSpec.For(PaymentsOverviewProjection.Declaration)
            .Given(Initiated(PaymentId))
            .When(new PaymentFailed(PaymentId, "card_declined", "Declined by issuer"))
            .ThenDocument(overview =>
            {
                overview.FailedPayments.Should().Be(1);
                overview.TotalCapturedAmount.Should().Be(0m);
            });
    }

    [Fact]
    public void Folds_every_payment_into_the_one_document_the_query_reads()
    {
        ProjectionSpec.For(PaymentsOverviewProjection.Declaration)
            .Given(Initiated(PaymentId), Initiated(OtherPaymentId))
            .ThenDocumentCount(1)
            .ThenDocument(PaymentsOverviewProjection.DocumentId, overview =>
                overview.TotalPayments.Should().Be(2));
    }
}
