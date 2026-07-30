using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class PaymentsOverviewTests
{
    [Fact]
    public void Declaration_handles_every_payment_event()
    {
        PaymentsOverviewProjection.Declaration.HandledEventTypes.Should().BeEquivalentTo(
            ["payment-initiated", "payment-authorized", "payment-captured", "payment-failed",
             "payment-refunded"]);
    }

    [Fact]
    public void Declaration_keeps_the_processor_id_its_checkpoint_and_state_rows_are_keyed_by()
    {
        PaymentsOverviewProjection.Declaration.ProcessorId
            .Should().Be(nameof(PaymentsOverviewProjection));
    }

    [Fact]
    public void Every_event_lands_on_the_one_document_the_query_reads()
    {
        // The writer's selectors and the reader's key are the same constant, so an aggregate
        // renamed on one side cannot quietly stop being found on the other.
        PaymentsOverviewProjection.DocumentId.Should().Be("overview");
    }

    [Fact]
    public void Overview_starts_empty()
    {
        var overview = new PaymentsOverview();

        overview.TotalPayments.Should().Be(0);
        overview.TotalCapturedAmount.Should().Be(0);
        overview.TotalRefundedAmount.Should().Be(0);
    }
}
