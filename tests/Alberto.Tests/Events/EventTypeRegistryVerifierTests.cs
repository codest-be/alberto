using System.Text.Json.Serialization;
using Alberto.Testing;
using Alberto.Tests.SampleEvents;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests;

// ---------------------------------------------------------------------------
// #180: the typed tag extractor overloads on EventTypeRegistryBuilder, and
// EventTypeRegistryVerifier, the test helper that catches a hand-written registry whose tags,
// id or version disagree with the [EventType]/[Tag] attributes it restates.
// ---------------------------------------------------------------------------

[EventType("verifier-transfer")]
public sealed record TransferMade(
    [property: Tag("from")] string FromAccount,
    [property: Tag("to")] string ToAccount,
    decimal Amount) : IEvent;

[EventType("verifier-shipment", Version = 3)]
public sealed record ShipmentBooked(
    [property: Tag("shipment")] Guid ShipmentId,
    [property: Tag("priority")] ShipmentPriority Priority,
    [property: Tag("slot")] int? Slot,
    string Carrier) : IEvent;

public enum ShipmentPriority { Low, High }

public readonly record struct CustomerId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Its tag is a type the verifier cannot fill, so it needs a sample.</summary>
[EventType("verifier-customer-renamed")]
public sealed record CustomerRenamed(
    [property: Tag("customer")] CustomerId CustomerId,
    string Name) : IEvent;

/// <summary>A get-only tag property: the probe has to fill its compiler-generated backing field.</summary>
[EventType("verifier-ledger-closed")]
public sealed class LedgerClosed(string ledger) : IEvent
{
    [Tag("ledger")] public string Ledger { get; } = ledger;
}

/// <summary>A computed tag property: nothing the probe can set.</summary>
[EventType("verifier-computed-tag")]
public sealed class ComputedTag : IEvent
{
    [Tag("kind")] public string Kind => "fixed";
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TransferMade))]
[JsonSerializable(typeof(ShipmentBooked))]
[JsonSerializable(typeof(CustomerRenamed))]
[JsonSerializable(typeof(LedgerClosed))]
[JsonSerializable(typeof(ComputedTag))]
internal sealed partial class VerifierJsonContext : JsonSerializerContext;

public class EventTypeRegistryVerifierTests
{
    private static readonly VerifierJsonContext Json = VerifierJsonContext.Default;

    private static EventTypeRegistryBuilder Correct() => EventTypeRegistry.CreateBuilder()
        .Add(Json.TransferMade, e => [new("from", e.FromAccount), new("to", e.ToAccount)])
        .Add(Json.ShipmentBooked, e => [new("shipment", e.ShipmentId), new("priority", e.Priority), new("slot", e.Slot)]);

    // ---- typed overloads ------------------------------------------------------------------

    [Fact]
    public void Typed_extractor_yields_the_same_tags_as_the_reflection_path()
    {
        var registry = Correct().Build();
        var transfer = new TransferMade("acc-1", "acc-2", 10m);

        EventSerializer.FromRegistry(registry).ExtractTags(transfer)
            .Should().Equal(EventSerializer.FromRegistry(EventTypeRegistry.CreateBuilder().Build()).ExtractTags(transfer));
    }

    [Fact]
    public void Typed_extractor_with_an_explicit_id_registers_that_id_and_version()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add("transfer-by-hand", Json.TransferMade, e => [new("from", e.FromAccount)], version: 4)
            .Build();

        registry.TryGetById("transfer-by-hand", out var d).Should().BeTrue();
        d!.Version.Should().Be(4);
        EventSerializer.FromRegistry(registry).ExtractTags(new TransferMade("a", "b", 1m))
            .Select(t => t.ToString()).Should().Equal("from:a", "_version:4");
    }

    [Fact]
    public void An_untyped_EventTagExtractor_still_binds_to_the_untyped_overload()
    {
        EventTagExtractor untyped = e => [new("from", ((TransferMade)e).FromAccount), new("to", ((TransferMade)e).ToAccount)];

        var registry = EventTypeRegistry.CreateBuilder().Add(Json.TransferMade, untyped).Build();

        registry.TryGetByType(typeof(TransferMade), out var d).Should().BeTrue();
        d!.TagExtractor.Should().BeSameAs(untyped);
    }

    [Fact]
    public void A_null_extractor_still_means_no_tags_on_either_overload()
    {
        // A literal null binds to the typed overload now; it has to keep its old meaning.
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.TransferMade, null)
            .Add("shipment-by-hand", Json.ShipmentBooked, null, version: 3)
            .Build();

        var serializer = EventSerializer.FromRegistry(registry);
        serializer.ExtractTags(new TransferMade("a", "b", 1m))
            .Select(t => t.ToString()).Should().Equal("_version:1");
        serializer.ExtractTags(new ShipmentBooked(Guid.Empty, ShipmentPriority.Low, null, "dhl"))
            .Select(t => t.ToString()).Should().Equal("_version:3");
    }

    // ---- verifier: agreeing registries ----------------------------------------------------

    [Fact]
    public void Verify_passes_a_registry_that_agrees_with_its_attributes_using_probe_events()
        => EventTypeRegistryVerifier.Verify(Correct().Build());

    [Fact]
    public void Verify_passes_the_hand_written_sample_registry_and_the_scan()
    {
        var hand = EventTypeRegistry.CreateBuilder()
            .Add(SampleEventsJsonContext.Default.VersionedOrder, e => [new("order", e.OrderId)])
            .Add(SampleEventsJsonContext.Default.PlainNote, e => [new("note", e.NoteId)])
            .Build();

        EventTypeRegistryVerifier.Verify(hand);
        EventTypeRegistryVerifier.Verify(EventTypeRegistry.FromAssembly(typeof(VersionedOrder).Assembly));
    }

    [Fact]
    public void Verify_uses_a_sample_for_a_type_it_cannot_probe()
    {
        var registry = Correct()
            .Add(Json.CustomerRenamed, e => [new("customer", e.CustomerId)])
            .Build();

        var withoutSample = () => EventTypeRegistryVerifier.Verify(registry);
        withoutSample.Should().Throw<SpecificationException>()
            .WithMessage("*'verifier-customer-renamed'*cannot build a probe event*CustomerId*Pass a sample*");

        EventTypeRegistryVerifier.Verify(registry, new CustomerRenamed(new CustomerId("c-7"), "Ada"));
    }

    [Fact]
    public void Verify_skips_the_attribute_check_for_a_type_registered_by_explicit_id_without_one()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add("unregistered", new UnregisteredJsonContext().UnregisteredEvent, e => [new("thing", e.Name)], version: 3)
            .Build();

        EventTypeRegistryVerifier.Verify(registry);
    }

    // ---- verifier: disagreeing registries -------------------------------------------------

    [Fact]
    public void Verify_catches_swapped_concepts()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.TransferMade, e => [new("from", e.ToAccount), new("to", e.FromAccount)])
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        act.Should().Throw<SpecificationException>()
            .WithMessage("*'verifier-transfer'*declared by [Tag] but not extracted: from:from-probe-1, to:to-probe-2*" +
                         "extracted but not declared by [Tag]: from:to-probe-2, to:from-probe-1*");
    }

    [Fact]
    public void Verify_catches_a_missing_and_an_undeclared_tag()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.ShipmentBooked, e => [new("shipment", e.ShipmentId), new("carrier", e.Carrier), new("slot", e.Slot)])
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry, new ShipmentBooked(Guid.Empty, ShipmentPriority.High, 4, "dhl"));

        act.Should().Throw<SpecificationException>()
            .WithMessage("*tags disagree on the sample*not extracted: priority:High*not declared by [Tag]: carrier:dhl*");
    }

    [Fact]
    public void Verify_catches_an_untyped_extractor_that_forgot_a_tag()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.TransferMade, (EventTagExtractor)(e => [new("from", ((TransferMade)e).FromAccount)]))
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        act.Should().Throw<SpecificationException>().WithMessage("*not extracted: to:to-probe-2*");
    }

    [Fact]
    public void Verify_catches_an_id_version_or_opt_out_that_disagrees_with_EventType()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add("verifier-transfer-v2", Json.TransferMade, e => [new("from", e.FromAccount), new("to", e.ToAccount)])
            .Add("verifier-shipment", Json.ShipmentBooked,
                e => [new("shipment", e.ShipmentId), new("priority", e.Priority), new("slot", e.Slot)],
                version: 2, upcastingNotRequired: true)
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        act.Should().Throw<SpecificationException>()
            .Where(ex => ex.Message.Contains("registered at version 2, but [EventType] declares version 3")
                         && ex.Message.Contains("UpcastingNotRequired = True, but [EventType] declares False")
                         && ex.Message.Contains("registered as id 'verifier-transfer-v2', but [EventType] declares 'verifier-transfer'"));
    }

    [Fact]
    public void Verify_reports_an_extractor_that_throws_on_the_probe()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.ShipmentBooked, e => [new("shipment", e.ShipmentId), new("priority", e.Priority), new("slot", e.Slot), new("carrier", e.Carrier.ToUpperInvariant())])
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        act.Should().Throw<SpecificationException>()
            .WithMessage("*'verifier-shipment'*threw NullReferenceException on a probe event*Pass a sample*");
    }

    [Fact]
    public void Verify_rejects_a_sample_of_an_unregistered_type_and_duplicate_samples()
    {
        var registry = Correct().Build();

        FluentActions.Invoking(() => EventTypeRegistryVerifier.Verify(registry, new CustomerRenamed(new CustomerId("c"), "n")))
            .Should().Throw<SpecificationException>().WithMessage("*CustomerRenamed: a sample was given, but the registry does not register this type*");

        FluentActions.Invoking(() => EventTypeRegistryVerifier.Verify(registry,
                new TransferMade("a", "b", 1m), new TransferMade("c", "d", 2m)))
            .Should().Throw<ArgumentException>().WithMessage("*More than one sample*");
    }

    [Fact]
    public void Verify_rejects_null_arguments()
    {
        var registry = Correct().Build();

        FluentActions.Invoking(() => EventTypeRegistryVerifier.Verify(null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("registry");
        FluentActions.Invoking(() => EventTypeRegistryVerifier.Verify(registry, null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("samples");
        FluentActions.Invoking(() => EventTypeRegistryVerifier.Verify(registry, [null!]))
            .Should().Throw<ArgumentNullException>().WithParameterName("samples");
    }

    [Fact]
    public void Verify_lists_failures_in_ordinal_id_order()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.TransferMade, e => [new("from", e.FromAccount)])
            .Add(Json.ShipmentBooked, e => [new("shipment", e.ShipmentId)])
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        act.Should().Throw<SpecificationException>()
            .WithMessage("*'verifier-shipment'*'verifier-transfer'*");
    }

    [Fact]
    public void Verify_reports_an_unprobeable_type_once_and_does_not_run_its_extractor()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.CustomerRenamed, e => [new("customer", e.CustomerId)])
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        act.Should().Throw<SpecificationException>()
            .Where(ex => ex.Message.Split('\n').Length == 2 && !ex.Message.Contains("threw"));
    }

    [Fact]
    public void Verify_probes_a_get_only_tag_property_through_its_backing_field()
    {
        // Without the backing field set, Ledger stays null and the extractor yields "unset",
        // which the reflection path does not.
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.LedgerClosed, e => [new("ledger", e.Ledger ?? "unset")])
            .Build();

        EventTypeRegistryVerifier.Verify(registry);
    }

    [Fact]
    public void Verify_asks_for_a_sample_when_a_tag_property_cannot_be_set()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.ComputedTag, e => [new("kind", e.Kind)])
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        act.Should().Throw<SpecificationException>()
            .WithMessage("*'verifier-computed-tag'*tag property 'Kind' has no setter or compiler-generated backing field*Pass a sample*");
    }

    [Fact]
    public void Builder_and_descriptor_reject_invalid_arguments()
    {
        FluentActions.Invoking(() => EventTypeRegistry.CreateBuilder().Add((EventTypeDescriptor)null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new EventTypeDescriptor("x", 0, false, Json.TransferMade, _ => []))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
