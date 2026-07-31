using System.Text.Json;
using Alberto.Dcb.Upcasting;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests;

// ---------------------------------------------------------------------------
// Domain fixtures — file-scoped to this test file.
// ---------------------------------------------------------------------------

// The v1 shape, as it was written to the log. No attribute: old versions exist only as the
// input to an upcast step, or — as here — as the thing the guard has to refuse to read.
file record GuardOrderV1(Guid OrderId, decimal Amount);

// The current shape. Region is new, and its default is not a value anyone chose.
[EventType("core2-guard-order", Version = 2)]
file record GuardOrderV2(Guid OrderId, decimal Amount, string Region) : IEvent;

// The same bump, waived at the declaration site: Region defaults to the region every order
// written before the bump was actually placed in, so v1 JSON reads correctly as-is.
[EventType("core2-waived-order", Version = 2, UpcastingNotRequired = true)]
file record WaivedOrderV2(Guid OrderId, decimal Amount, string Region = "eu-west-1") : IEvent;

// A type that never moved past v1 — the case the guard must stay silent for.
[EventType("core2-stable-order")]
file record StableOrder(Guid OrderId, decimal Amount) : IEvent;

// Declared at v3, with only a 1→2 chain registered for it: the shape a chain that was never
// extended alongside the attribute leaves behind.
[EventType("core2-short-order", Version = 3)]
file record ShortChainOrderV3(Guid OrderId, decimal Amount, string Region, string Channel) : IEvent;

file static class GuardFixtures
{
    public const string GuardedSlug = "core2-guard-order";
    public const string WaivedSlug = "core2-waived-order";
    public const string StableSlug = "core2-stable-order";
    public const string ShortChainSlug = "core2-short-order";

    /// <summary>
    /// FromRegistry rather than FromAssembly: the test assembly carries duplicate
    /// <c>[EventType]</c> slugs across test files, so an assembly scan cannot succeed here.
    /// </summary>
    public static EventSerializer Serializer(params (string Slug, Type Type)[] types) =>
        EventSerializer.FromRegistry(types.ToDictionary(t => t.Slug, t => t.Type));

    public static IEventEnvelope Envelope(string slug, int version, object payload) => new EventEnvelope
    {
        Id = Guid.NewGuid(),
        GlobalPosition = 1,
        EventType = new EventType(slug, version),
        EventData = JsonSerializer.Serialize(payload),
        Tags = [],
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.UtcNow,
        TenantId = null,
    };
}

/// <summary>
/// CORE-2: <see cref="EventSerializer.Deserialize"/> refuses an envelope stored below the
/// version its CLR type declares unless something covers the gap.
///
/// <para>
/// <c>ALB0018</c> already catches the missing upcaster — but only on the DI path, at startup.
/// A serializer built by hand (<c>EventSerializer.FromAssembly(...)</c> in a migration script, a
/// test helper, a one-off tool) never meets the validator, and the failure it lets through is
/// silent: JSON leaves every member the older payload lacks at its CLR default, so an evolver
/// folds a 0 or a <see langword="null"/> as though it had been stored. These tests pin the guard
/// to the serializer itself, where it cannot be bypassed.
/// </para>
/// </summary>
public sealed class EventSerializerVersionGuardTests
{
    // ── the gap is refused ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_older_envelope_with_no_upcaster_throws()
    {
        var serializer = GuardFixtures.Serializer((GuardFixtures.GuardedSlug, typeof(GuardOrderV2)));
        var envelope = GuardFixtures.Envelope(
            GuardFixtures.GuardedSlug, version: 1, new GuardOrderV1(Guid.NewGuid(), 42m));

        var act = () => serializer.Deserialize(envelope);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*stored at schema version 1*")
            .WithMessage("*Version = 2*")
            .WithMessage("*No upcaster is registered*")
            .WithMessage("*UpcastingNotRequired = true*",
                "the message has to name both remedies — write the upcaster, or waive the gap");
    }

    [Fact]
    public void Deserialize_without_the_guard_would_have_produced_a_default_region()
    {
        // The point of the guard, stated as a test: this is what the caller used to get back.
        // Region is not nullable and nothing in the v1 JSON supplies it, so System.Text.Json
        // hands the evolver a null in a string-typed slot and nothing anywhere complains.
        var raw = JsonSerializer.Deserialize<GuardOrderV2>(
            JsonSerializer.Serialize(new GuardOrderV1(Guid.NewGuid(), 42m)));

        raw!.Region.Should().BeNull(
            "which is exactly the silent corruption the guard exists to turn into an exception");
    }

    [Fact]
    public void Deserialize_envelope_at_the_end_of_a_chain_that_stops_short_of_the_type_throws()
    {
        // ALB0020's runtime counterpart. The chain runs 1→2 and terminates cleanly, so nothing in
        // the upcasting machinery objects — but the attribute says 3. A v2 envelope is at the
        // chain's own current version, so no step fires and the payload goes straight into the v3
        // shape, Channel silently null.
        var chainStoppingAtV2 = UpcasterRegistry.Create()
            .Add(DeclareUpcaster
                .For<ShortChainOrderV3>(GuardFixtures.ShortChainSlug)
                .From<GuardOrderV1>(1, v1 =>
                    new ShortChainOrderV3(v1.OrderId, v1.Amount, "eu-west-1", "web"))
                .Build())
            .Build();

        var serializer = GuardFixtures
            .Serializer((GuardFixtures.ShortChainSlug, typeof(ShortChainOrderV3)))
            .WithUpcasters(chainStoppingAtV2);

        var envelope = GuardFixtures.Envelope(
            GuardFixtures.ShortChainSlug, version: 2, new GuardOrderV2(Guid.NewGuid(), 42m, "eu-west-1"));

        serializer.Invoking(s => s.Deserialize(envelope))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*upcaster chain stops at version 2*");
    }

    // ── the gap is covered ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_older_envelope_with_a_covering_upcaster_upcasts_as_before()
    {
        var upcasters = UpcasterRegistry.Create()
            .Add(DeclareUpcaster
                .For<GuardOrderV2>(GuardFixtures.GuardedSlug)
                .From<GuardOrderV1>(1, v1 => new GuardOrderV2(v1.OrderId, v1.Amount, "eu-west-1"))
                .Build())
            .Build();

        var serializer = GuardFixtures
            .Serializer((GuardFixtures.GuardedSlug, typeof(GuardOrderV2)))
            .WithUpcasters(upcasters);

        var orderId = Guid.NewGuid();
        var result = serializer.Deserialize(GuardFixtures.Envelope(
            GuardFixtures.GuardedSlug, version: 1, new GuardOrderV1(orderId, 42m)));

        result.Should().BeOfType<GuardOrderV2>()
            .Which.Region.Should().Be("eu-west-1");
    }

    [Fact]
    public void Deserialize_older_envelope_of_a_type_that_waived_upcasting_succeeds()
    {
        var serializer = GuardFixtures.Serializer((GuardFixtures.WaivedSlug, typeof(WaivedOrderV2)));

        var orderId = Guid.NewGuid();
        var result = serializer.Deserialize(GuardFixtures.Envelope(
            GuardFixtures.WaivedSlug, version: 1, new GuardOrderV1(orderId, 42m)));

        var order = result.Should().BeOfType<WaivedOrderV2>().Which;
        order.OrderId.Should().Be(orderId);
        order.Region.Should().Be("eu-west-1", "the property default is the waiver's whole premise");
    }

    // ── the guard stays out of the way ─────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_current_version_envelope_is_untouched()
    {
        var serializer = GuardFixtures.Serializer((GuardFixtures.GuardedSlug, typeof(GuardOrderV2)));

        var result = serializer.Deserialize(GuardFixtures.Envelope(
            GuardFixtures.GuardedSlug, version: 2, new GuardOrderV2(Guid.NewGuid(), 42m, "us-east-1")));

        result.Should().BeOfType<GuardOrderV2>().Which.Region.Should().Be("us-east-1");
    }

    [Fact]
    public void Deserialize_version_1_type_needs_no_declaration_at_all()
    {
        var serializer = GuardFixtures.Serializer((GuardFixtures.StableSlug, typeof(StableOrder)));

        var orderId = Guid.NewGuid();
        var result = serializer.Deserialize(GuardFixtures.Envelope(
            GuardFixtures.StableSlug, version: 1, new StableOrder(orderId, 42m)));

        result.Should().BeOfType<StableOrder>().Which.OrderId.Should().Be(orderId);
    }

    [Fact]
    public void Deserialize_newer_envelope_than_the_running_code_still_reads()
    {
        // Deliberately not guarded. A rolling deploy has old instances reading events the new
        // ones already write; System.Text.Json ignores the members they do not know about, which
        // is the behaviour that makes such a deploy survivable. Only the downward gap corrupts.
        var serializer = GuardFixtures.Serializer((GuardFixtures.StableSlug, typeof(StableOrder)));

        var orderId = Guid.NewGuid();
        var result = serializer.Deserialize(GuardFixtures.Envelope(
            GuardFixtures.StableSlug, version: 2, new GuardOrderV2(orderId, 42m, "us-east-1")));

        result.Should().BeOfType<StableOrder>().Which.OrderId.Should().Be(orderId);
    }
}
