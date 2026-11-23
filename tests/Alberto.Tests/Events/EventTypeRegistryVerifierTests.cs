using System.Text.Json.Serialization;
using Alberto.Testing;
using Alberto.Tests.SampleEvents;
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

        Assert.Equal(EventSerializer.FromRegistry(EventTypeRegistry.CreateBuilder().Build()).ExtractTags(transfer), EventSerializer.FromRegistry(registry).ExtractTags(transfer));
    }

    [Fact]
    public void Typed_extractor_with_an_explicit_id_registers_that_id_and_version()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add("transfer-by-hand", Json.TransferMade, e => [new("from", e.FromAccount)], version: 4)
            .Build();

        Assert.True(registry.TryGetById("transfer-by-hand", out var d));
        Assert.Equal(4, d!.Version);
        Assert.Equal(["from:a", "_version:4"], EventSerializer.FromRegistry(registry).ExtractTags(new TransferMade("a", "b", 1m))
            .Select(t => t.ToString()));
    }

    [Fact]
    public void An_untyped_EventTagExtractor_still_binds_to_the_untyped_overload()
    {
        EventTagExtractor untyped = e => [new("from", ((TransferMade)e).FromAccount), new("to", ((TransferMade)e).ToAccount)];

        var registry = EventTypeRegistry.CreateBuilder().Add(Json.TransferMade, untyped).Build();

        Assert.True(registry.TryGetByType(typeof(TransferMade), out var d));
        Assert.Same(untyped, d!.TagExtractor);
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
        Assert.Equal(["_version:1"], serializer.ExtractTags(new TransferMade("a", "b", 1m))
            .Select(t => t.ToString()));
        Assert.Equal(["_version:3"], serializer.ExtractTags(new ShipmentBooked(Guid.Empty, ShipmentPriority.Low, null, "dhl"))
            .Select(t => t.ToString()));
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
        var exception = Assert.ThrowsAny<SpecificationException>(withoutSample);
        Assert.Matches("(?is)'verifier-customer-renamed'.*cannot build a probe event.*CustomerId.*Pass a sample", exception.Message);

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

        var exception = Assert.ThrowsAny<SpecificationException>(act);
        Assert.Matches(
            @"(?is)'verifier-transfer'.*declared by \[Tag\] but not extracted: from:from-probe-1, to:to-probe-2.*" +
            @"extracted but not declared by \[Tag\]: from:to-probe-2, to:from-probe-1",
            exception.Message);
    }

    [Fact]
    public void Verify_catches_a_missing_and_an_undeclared_tag()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.ShipmentBooked, e => [new("shipment", e.ShipmentId), new("carrier", e.Carrier), new("slot", e.Slot)])
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry, new ShipmentBooked(Guid.Empty, ShipmentPriority.High, 4, "dhl"));

        var exception = Assert.ThrowsAny<SpecificationException>(act);
        Assert.Matches("(?is)tags disagree on the sample.*not extracted: priority:High.*not declared by \\[Tag]: carrier:dhl", exception.Message);
    }

    [Fact]
    public void Verify_catches_an_untyped_extractor_that_forgot_a_tag()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.TransferMade, (EventTagExtractor)(e => [new("from", ((TransferMade)e).FromAccount)]))
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        var exception = Assert.ThrowsAny<SpecificationException>(act);
        Assert.Contains("not extracted: to:to-probe-2", exception.Message, StringComparison.OrdinalIgnoreCase);
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

        var exception = Assert.ThrowsAny<SpecificationException>(act);
        Assert.True(exception.Message.Contains("registered at version 2, but [EventType] declares version 3")
                         && exception.Message.Contains("UpcastingNotRequired = True, but [EventType] declares False")
                         && exception.Message.Contains("registered as id 'verifier-transfer-v2', but [EventType] declares 'verifier-transfer'"));
    }

    [Fact]
    public void Verify_reports_an_extractor_that_throws_on_the_probe()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.ShipmentBooked, e => [new("shipment", e.ShipmentId), new("priority", e.Priority), new("slot", e.Slot), new("carrier", e.Carrier.ToUpperInvariant())])
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        var exception = Assert.ThrowsAny<SpecificationException>(act);
        Assert.Matches("(?is)'verifier-shipment'.*threw NullReferenceException on a probe event.*Pass a sample", exception.Message);
    }

    [Fact]
    public void Verify_rejects_a_sample_of_an_unregistered_type_and_duplicate_samples()
    {
        var registry = Correct().Build();

        var exception = Assert.ThrowsAny<SpecificationException>(() => EventTypeRegistryVerifier.Verify(registry, new CustomerRenamed(new CustomerId("c"), "n")));
        Assert.Contains("CustomerRenamed: a sample was given, but the registry does not register this type", exception.Message, StringComparison.OrdinalIgnoreCase);

        var exception2 = Assert.ThrowsAny<ArgumentException>(() => EventTypeRegistryVerifier.Verify(registry,
                new TransferMade("a", "b", 1m), new TransferMade("c", "d", 2m)));
        Assert.Contains("More than one sample", exception2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_rejects_null_arguments()
    {
        var registry = Correct().Build();

        var exception = Assert.ThrowsAny<ArgumentNullException>(() => EventTypeRegistryVerifier.Verify(null!));
        Assert.Equal("registry", exception.ParamName);
        var exception2 = Assert.ThrowsAny<ArgumentNullException>(() => EventTypeRegistryVerifier.Verify(registry, null!));
        Assert.Equal("samples", exception2.ParamName);
        var exception3 = Assert.ThrowsAny<ArgumentNullException>(() => EventTypeRegistryVerifier.Verify(registry, [null!]));
        Assert.Equal("samples", exception3.ParamName);
    }

    [Fact]
    public void Verify_lists_failures_in_ordinal_id_order()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.TransferMade, e => [new("from", e.FromAccount)])
            .Add(Json.ShipmentBooked, e => [new("shipment", e.ShipmentId)])
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        var exception = Assert.ThrowsAny<SpecificationException>(act);
        Assert.Matches("(?is)'verifier-shipment'.*'verifier-transfer'", exception.Message);
    }

    [Fact]
    public void Verify_reports_an_unprobeable_type_once_and_does_not_run_its_extractor()
    {
        var registry = EventTypeRegistry.CreateBuilder()
            .Add(Json.CustomerRenamed, e => [new("customer", e.CustomerId)])
            .Build();

        var act = () => EventTypeRegistryVerifier.Verify(registry);

        var exception = Assert.ThrowsAny<SpecificationException>(act);
        Assert.True(exception.Message.Split('\n').Length == 2 && !exception.Message.Contains("threw"));
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

        var exception = Assert.ThrowsAny<SpecificationException>(act);
        Assert.Matches("(?is)'verifier-computed-tag'.*tag property 'Kind' has no setter or compiler-generated backing field.*Pass a sample", exception.Message);
    }

    [Fact]
    public void Builder_and_descriptor_reject_invalid_arguments()
    {
        Assert.ThrowsAny<ArgumentNullException>(() => EventTypeRegistry.CreateBuilder().Add((EventTypeDescriptor)null!));
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new EventTypeDescriptor("x", 0, false, Json.TransferMade, _ => []));
    }
}
