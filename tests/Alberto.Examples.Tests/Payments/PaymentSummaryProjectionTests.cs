using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

/// <summary>
/// The per-payment read model folded on its own: events in, documents out.
/// </summary>
public sealed class PaymentSummaryProjectionTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c006-0000-7000-8000-000000000001");
    private static readonly Guid OtherPaymentId = Guid.Parse("0197c006-0000-7000-8000-000000000011");
    private static readonly Guid OrderId = Guid.Parse("0197c006-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Document(Guid paymentId) => paymentId.ToString();

    private static PaymentInitiated Initiated(Guid paymentId) =>
        new(paymentId, OrderId, 49.95m, "EUR", "card");

    [Fact]
    public void Opens_a_summary_when_a_payment_is_initiated()
    {
        ProjectionSpec.For(PaymentSummaryProjection.Declaration)
            .GivenNoEvents()
            .When(Initiated(PaymentId))
            .ThenDocument(Document(PaymentId), summary =>
            {
                summary.PaymentId.Should().Be(PaymentId);
                summary.OrderId.Should().Be(OrderId);
                summary.Amount.Should().Be(49.95m);
                summary.Currency.Should().Be("EUR");
                summary.PaymentMethod.Should().Be("card");
                summary.Status.Should().Be(PaymentSummaryStatus.Initiated);
                summary.CreatedAt.Should().Be(ProjectionSpec.Epoch);
            });
    }

    [Fact]
    public void Records_the_authorization_code_the_provider_returned()
    {
        ProjectionSpec.For(PaymentSummaryProjection.Declaration)
            .Given(Initiated(PaymentId))
            .When(new PaymentAuthorized(PaymentId, "AUTH-1", Now))
            .ThenDocument(summary =>
            {
                summary.Status.Should().Be(PaymentSummaryStatus.Authorized);
                summary.AuthorizationCode.Should().Be("AUTH-1");
                summary.AuthorizedAt.Should().Be(Now);
            });
    }

    [Fact]
    public void Carries_the_authorization_forward_when_the_payment_is_captured()
    {
        ProjectionSpec.For(PaymentSummaryProjection.Declaration)
            .Given(Initiated(PaymentId), new PaymentAuthorized(PaymentId, "AUTH-1", Now))
            .When(new PaymentCaptured(PaymentId, 49.95m, Now))
            .ThenDocument(summary =>
            {
                summary.Status.Should().Be(PaymentSummaryStatus.Captured);
                summary.CapturedAt.Should().Be(Now);
                summary.AuthorizationCode.Should().Be("AUTH-1");
            });
    }

    [Fact]
    public void Records_why_a_payment_failed()
    {
        ProjectionSpec.For(PaymentSummaryProjection.Declaration)
            .Given(Initiated(PaymentId))
            .When(new PaymentFailed(PaymentId, "card_declined", "Declined by issuer"))
            .ThenDocument(summary =>
            {
                summary.Status.Should().Be(PaymentSummaryStatus.Failed);
                summary.ErrorCode.Should().Be("card_declined");
                summary.ErrorMessage.Should().Be("Declined by issuer");
            });
    }

    [Fact]
    public void Records_a_refund_against_the_captured_payment()
    {
        ProjectionSpec.For(PaymentSummaryProjection.Declaration)
            .Given(
                Initiated(PaymentId),
                new PaymentAuthorized(PaymentId, "AUTH-1", Now),
                new PaymentCaptured(PaymentId, 49.95m, Now))
            .When(new PaymentRefunded(PaymentId, 12.50m, "damaged", Now))
            .ThenDocument(summary =>
            {
                summary.Status.Should().Be(PaymentSummaryStatus.Refunded);
                summary.RefundedAmount.Should().Be(12.50m);
                summary.RefundedAt.Should().Be(Now);
            });
    }

    [Fact]
    public void Keys_one_document_per_payment_and_keeps_them_apart()
    {
        ProjectionSpec.For(PaymentSummaryProjection.Declaration)
            .Given(Initiated(PaymentId), Initiated(OtherPaymentId))
            .When(new PaymentFailed(PaymentId, "card_declined", "Declined by issuer"))
            .ThenDocumentCount(2)
            .ThenDocument(Document(PaymentId), s => s.Status.Should().Be(PaymentSummaryStatus.Failed))
            .ThenDocument(Document(OtherPaymentId), s => s.Status.Should().Be(PaymentSummaryStatus.Initiated));
    }

    [Fact]
    public void Starts_a_summary_from_whatever_event_reaches_it_first()
    {
        // A projection added after the payments already existed, or a rebuild from a truncated
        // log, sees an authorization with no initiation behind it. It writes a document rather
        // than dropping the event — the fields the initiation would have filled stay default.
        ProjectionSpec.For(PaymentSummaryProjection.Declaration)
            .GivenNoEvents()
            .When(new PaymentAuthorized(PaymentId, "AUTH-1", Now))
            .ThenDocument(summary =>
            {
                summary.Status.Should().Be(PaymentSummaryStatus.Authorized);
                summary.PaymentId.Should().Be(Guid.Empty);
            });
    }
}
