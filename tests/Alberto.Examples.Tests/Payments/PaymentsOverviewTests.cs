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
    public void Document_id_is_the_storage_key_and_must_not_change()
    {
        // The writer's selectors and the reader's key are the same constant, so an aggregate
        // renamed on one side cannot quietly stop being found on the other — and changing it
        // orphans the rows already written under the old key in a deployed store.
        PaymentsOverviewProjection.DocumentId.Should().Be("overview");
    }
}
