using Alberto.Upcasting;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests;

// ---------------------------------------------------------------------------
// Domain fixtures specific to these tests.
//
// An event type that declares Version = 2.  Any envelope carrying version = 1
// for this slug represents a pre-migration record that requires upcasting.
// ---------------------------------------------------------------------------

file record PublicApiOrderV1(Guid OrderId, decimal Amount);

[EventType("public-api-upcast-order", Version = 2)]
file record PublicApiOrderV2(Guid OrderId, decimal Amount, string? Region) : IEvent;

file record PublicApiOrderState
{
    public string? LastRegion { get; init; }
    public PublicApiOrderState() { }
}

file sealed class PublicApiOrderEvolver : Evolver<PublicApiOrderState>,
    IEvolve<PublicApiOrderState, PublicApiOrderV2>
{
    public PublicApiOrderState Apply(PublicApiOrderState state, PublicApiOrderV2 e)
        => state with { LastRegion = e.Region };
}

// ---------------------------------------------------------------------------
// Helper to build a synthetic envelope at an explicit schema version.
// ---------------------------------------------------------------------------

file sealed record SyntheticEnvelope(string Slug, int Version, string Json) : IEventEnvelope
{
    public Guid Id { get; } = Guid.NewGuid();
    public string? TenantId => null;
    public long GlobalPosition { get; } = 1;
    public EventType EventType => new(Slug, Version);
    public IReadOnlyCollection<EventTag> Tags { get; } = [];
    public string EventData => Json;
    public IReadOnlyDictionary<string, string> Metadata { get; } = new Dictionary<string, string>();
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
}

// ---------------------------------------------------------------------------
// Tests: Evolver.Reconstitute(envelopes) — the public no-serializer overload.
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that the public
/// <see cref="Evolver{TState}.Reconstitute(System.Collections.Generic.IEnumerable{Alberto.IEventEnvelope}, TState)"/>
/// overload (no serializer) throws a clear <see cref="InvalidOperationException"/> when an
/// envelope's stored schema version is older than the version declared on the handler's CLR type.
///
/// Previously this scenario produced silently wrong state. After the EV-1 guard the evolver
/// fails loud rather than returning a stale shape, ensuring callers notice and supply a serializer.
/// </summary>
public sealed class EvolverPublicApiUpcastingTests
{
    private const string Slug = "public-api-upcast-order";

    [Fact]
    public void Reconstitute_WithStaleVersionEnvelope_ThrowsInvalidOperationException()
    {
        // Arrange: evolver handles PublicApiOrderV2 (declared Version = 2).
        // Feed it a v1 envelope — the pre-migration shape that should have been upcasted.
        var evolver = new PublicApiOrderEvolver();
        var orderId = Guid.NewGuid();
        var envelopes = new IEventEnvelope[]
        {
            new SyntheticEnvelope(Slug, Version: 1,
                Json: $$"""{"OrderId":"{{orderId:D}}","Amount":99.0}""")
        };

        // Act
        var act = () => evolver.Reconstitute(envelopes);

        // Assert: must throw, not silently return wrong state.
        act.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("*schema version 1*")
            .And.Message.Should().Contain("version 2",
                "the error must state both the stored version and the expected version");
    }

    [Fact]
    public void Reconstitute_WithStaleVersionEnvelope_ErrorMessageNamesCorrectAPI()
    {
        // The exception message must guide the caller toward the serializer-threaded overload
        // so they know the correct fix without having to search the docs.
        var evolver = new PublicApiOrderEvolver();
        var envelopes = new IEventEnvelope[]
        {
            new SyntheticEnvelope(Slug, Version: 1, Json: """{"OrderId":"00000000-0000-0000-0000-000000000001","Amount":1.0}""")
        };

        var act = () => evolver.Reconstitute(envelopes);

        act.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("*EventSerializer*",
                "the error must name the serializer-threaded alternative so the caller knows the correct fix");
    }

    [Fact]
    public void Reconstitute_WithCurrentVersionEnvelope_ReturnsCorrectState()
    {
        // A v2 envelope (already at current version) must still work through the
        // public no-serializer overload — the guard only fires for stale versions.
        var evolver = new PublicApiOrderEvolver();
        var orderId = Guid.NewGuid();
        var envelopes = new IEventEnvelope[]
        {
            new SyntheticEnvelope(Slug, Version: 2,
                Json: $$"""{"OrderId":"{{orderId:D}}","Amount":10.0,"Region":"direct"}""")
        };

        var state = evolver.Reconstitute(envelopes);

        state.LastRegion.Should().Be("direct");
    }

    [Fact]
    public void Evolve_SingleEnvelope_WithStaleVersion_ThrowsInvalidOperationException()
    {
        // Evolver.Evolve(state, envelope) routes through the same EvolverDispatcher.Handler
        // code path as Reconstitute — verify the guard fires there too.
        var evolver = new PublicApiOrderEvolver();
        var staleEnvelope = new SyntheticEnvelope(Slug, Version: 1,
            Json: """{"OrderId":"00000000-0000-0000-0000-000000000002","Amount":5.0}""");

        var act = () => evolver.Evolve(new PublicApiOrderState(), staleEnvelope);

        act.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("*schema version 1*");
    }

    [Fact]
    public void Reconstitute_WithSerializer_StillUpcasts_WhenVersionIsStale()
    {
        // Confirm that the CORRECT path (with serializer) still works fine after the guard is in
        // place — the guard must not interfere with the serializer-threaded overload.
        var evolver = new PublicApiOrderEvolver();
        var orderId = Guid.NewGuid();
        var envelopes = new IEventEnvelope[]
        {
            new SyntheticEnvelope(Slug, Version: 1,
                Json: $$"""{"OrderId":"{{orderId:D}}","Amount":7.0}""")
        };

        var serializer = EventSerializer
            .FromRegistry(new Dictionary<string, Type> { [Slug] = typeof(PublicApiOrderV2) })
            .WithUpcasters(UpcasterRegistry.Create()
                .Add(DeclareUpcaster
                    .For<PublicApiOrderV2>(Slug)
                    .From<PublicApiOrderV1>(1, v1 => new PublicApiOrderV2(v1.OrderId, v1.Amount, "via-upcaster"))
                    .Build())
                .Build());

        // The internal Reconstitute(envelopes, initial, deserialize) overload bypasses the guard
        // because it supplies a non-null deserializer.
        var state = evolver.Reconstitute(envelopes, initial: default, serializer.Deserialize);

        state.LastRegion.Should().Be("via-upcaster",
            "the serializer-threaded overload must apply the upcaster chain correctly");
    }
}
