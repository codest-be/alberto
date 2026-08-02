using Alberto.Commands;
using Alberto.InMemory;
using Alberto.Upcasting;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests;

// ---------------------------------------------------------------------------
// Domain fixtures
//
// Convention (mirrors production use):
//   - Only the CURRENT (latest) version of an event type carries [EventType].
//   - Old versions used exclusively in upcast chains are plain records: no attribute.
// ---------------------------------------------------------------------------

// Used by end-to-end and unit tests for the upcaster → evolver path.
file record UpcastEvolverOrderV1(Guid OrderId, decimal Amount);

[EventType("upcast-evolver-order", Version = 2)]
file record UpcastEvolverOrderV2(Guid OrderId, decimal Amount, string? Region) : IEvent;

// Used for the type-mismatch guard test — a separate CLR type with the same slug.
// Implements IEvent so EventSerializer can return it; no [EventType] attribute
// needed because the registry is built manually in the test.
file record UpcastEvolverWrongType(string Tag) : IEvent;

// ---------------------------------------------------------------------------
// State and evolver shared across these tests.
// ---------------------------------------------------------------------------

file record UpcastEvolverState
{
    public string? LastRegion { get; init; }

    // Parameterless constructor required by Evolver<TState>.
    public UpcastEvolverState() { }
}

file sealed class UpcastEvolverOrderEvolver : Evolver<UpcastEvolverState>,
    IEvolve<UpcastEvolverState, UpcastEvolverOrderV2>
{
    public UpcastEvolverState Apply(UpcastEvolverState state, UpcastEvolverOrderV2 e)
        => state with { LastRegion = e.Region };
}

// ---------------------------------------------------------------------------
// 1. End-to-end: command pipeline drives the primary read path
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that the upcaster chain fires on the primary command path
/// (CommandPipeline.Handle → Load(evolver) → AlbertoStore.ReconstituteWithPosition).
/// This test FAILS against the pre-fix code because EvolverDispatcher fell back to
/// raw JsonSerializer.Deserialize, bypassing EventSerializer and its upcaster chain.
/// </summary>
public sealed class EvolverUpcastingCommandPathTests
{
    private static readonly string Slug = "upcast-evolver-order";

    private static EventSerializer BuildSerializer() =>
        EventSerializer
            .FromRegistry(new Dictionary<string, Type>
            {
                [Slug] = typeof(UpcastEvolverOrderV2)
            })
            .WithUpcasters(UpcasterRegistry.Create()
                .Add(DeclareUpcaster
                    .For<UpcastEvolverOrderV2>(Slug)
                    .From<UpcastEvolverOrderV1>(
                        1,
                        v1 => new UpcastEvolverOrderV2(v1.OrderId, v1.Amount, "upcasted-region"))
                    .Build())
                .Build());

    [Fact]
    public async Task CommandPipeline_LoadWithEvolver_UpcasterFires_BeforeHandlerSeesEvent()
    {
        // Arrange: seed the store with a v1 envelope (missing Region field).
        var orderId = Guid.NewGuid();
        var eventStore = new EventStore(new InMemoryEventStoreBackend());

        await eventStore.AppendAsync([
            new EventToPersist
            {
                EventType = new EventType(Slug, version: 1),
                Tags = [],
                EventData = $$"""{"OrderId":"{{orderId:D}}","Amount":42.0}""",
                Metadata = new Dictionary<string, string>()
            }
        ]);

        var serializer = BuildSerializer();
        var store = new AlbertoStore(eventStore, serializer);
        var evolver = new UpcastEvolverOrderEvolver();
        string? observedRegion = null;

        var boundary = DcbQuery.ByTypes(Slug);

        // Act: full Handle → Load(evolver) → Decide → Commit pipeline.
        var result = await store
            .Handle(orderId)
            .Load(boundary, evolver)
            .Decide((_, state) =>
            {
                observedRegion = state.LastRegion;
                return Decision.Succeed();
            })
            .Commit(CancellationToken.None);

        // Assert: the evolver received the UPCASTED shape, not the raw v1 JSON.
        result.IsSuccess.Should().BeTrue();
        observedRegion.Should().Be(
            "upcasted-region",
            "the upcaster chain must run on the command path before the evolver handler sees the event");
    }

    [Fact]
    public async Task CommandPipeline_LoadWithEvolver_CurrentVersionEvent_PassesThroughUnmodified()
    {
        // A v2 event (already at current version) must not be corrupted by the upcaster path.
        var orderId = Guid.NewGuid();
        var eventStore = new EventStore(new InMemoryEventStoreBackend());

        await eventStore.AppendAsync([
            new EventToPersist
            {
                EventType = new EventType(Slug, version: 2),
                // EventTag.ForVersion stamps the _version:2 reserved tag so the backend reads
                // the event back as schema version 2 rather than defaulting to v1.
                // Storage is the source of truth for version; without the tag the backend
                // defaults to v1 and the upcaster fires, corrupting the event.
                Tags = [EventTag.ForVersion(2)],
                EventData = $$"""{"OrderId":"{{orderId:D}}","Amount":10.0,"Region":"original-region"}""",
                Metadata = new Dictionary<string, string>()
            }
        ]);

        var serializer = BuildSerializer();
        var store = new AlbertoStore(eventStore, serializer);
        var evolver = new UpcastEvolverOrderEvolver();
        string? observedRegion = null;

        var result = await store
            .Handle(orderId)
            .Load(DcbQuery.ByTypes(Slug), evolver)
            .Decide((_, state) =>
            {
                observedRegion = state.LastRegion;
                return Decision.Succeed();
            })
            .Commit(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        observedRegion.Should().Be("original-region");
    }
}

// ---------------------------------------------------------------------------
// 2. Type-mismatch guard
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies the clear-error path when the upcaster chain returns a type that does not
/// match the type declared in the evolver's IEvolve&lt;TState, TEvent&gt; handler.
/// </summary>
public sealed class EvolverTypeMismatchTests
{
    private static readonly string Slug = "upcast-evolver-order";

    [Fact]
    public async Task AlbertoStore_Reconstitute_ThrowsClearException_WhenUpcasterReturnsMismatchedType()
    {
        // Arrange: serializer whose upcaster for the slug returns UpcastEvolverWrongType,
        // but the evolver declares IEvolve<TState, UpcastEvolverOrderV2>.
        var orderId = Guid.NewGuid();
        var eventStore = new EventStore(new InMemoryEventStoreBackend());

        await eventStore.AppendAsync([
            new EventToPersist
            {
                EventType = new EventType(Slug, version: 1),
                Tags = [],
                EventData = $$"""{"OrderId":"{{orderId:D}}","Amount":1.0}""",
                Metadata = new Dictionary<string, string>()
            }
        ]);

        // Intentionally map the slug to WrongType in both registry and upcaster.
        var mismatchSerializer = EventSerializer
            .FromRegistry(new Dictionary<string, Type>
            {
                [Slug] = typeof(UpcastEvolverWrongType)
            })
            .WithUpcasters(UpcasterRegistry.Create()
                .Add(DeclareUpcaster
                    .For<UpcastEvolverWrongType>(Slug)
                    .From<UpcastEvolverOrderV1>(1, _ => new UpcastEvolverWrongType("wrong"))
                    .Build())
                .Build());

        var store = new AlbertoStore(eventStore, mismatchSerializer);

        // Act & Assert: the error must name both types so the developer knows exactly what went wrong.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.Handle(orderId)
                .Load(DcbQuery.ByTypes(Slug), new UpcastEvolverOrderEvolver())
                .Decide((_, _) => Decision.Succeed())
                .Commit(CancellationToken.None));

        ex.Message.Should().Contain(
            nameof(UpcastEvolverWrongType),
            "the error must identify the type the upcaster actually returned");
        ex.Message.Should().Contain(
            nameof(UpcastEvolverOrderV2),
            "the error must identify the type the handler expected");
    }
}

// ---------------------------------------------------------------------------
// 3. DeciderExtensions — new overload with EventSerializer
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that the <see cref="EventSerializer"/>-taking
/// <see cref="DeciderExtensions.DecideAndAppendAsync{TState}(Alberto.IEventStore, Alberto.DcbQuery, Alberto.Evolver{TState}, System.Func{TState, Alberto.Decision}, System.Func{Alberto.IEvent, Alberto.IEventToPersist}, Alberto.EventSerializer, System.Threading.CancellationToken)"/>
/// applies the upcaster chain on reconstitution,
/// and that the existing overload (no serializer) still compiles and falls back to raw JSON.
/// </summary>
public sealed class DeciderExtensionsUpcasterTests
{
    private static readonly string Slug = "upcast-evolver-order";

    [Fact]
    public async Task DecideAndAppendAsync_WithSerializer_AppliesUpcasterChain()
    {
        // Arrange: v1 event in store.
        var orderId = Guid.NewGuid();
        var backend = new InMemoryEventStoreBackend();
        var eventStore = new EventStore(backend);

        await eventStore.AppendAsync([
            new EventToPersist
            {
                EventType = new EventType(Slug, version: 1),
                Tags = [],
                EventData = $$"""{"OrderId":"{{orderId:D}}","Amount":5.0}""",
                Metadata = new Dictionary<string, string>()
            }
        ]);

        var serializer = EventSerializer
            .FromRegistry(new Dictionary<string, Type> { [Slug] = typeof(UpcastEvolverOrderV2) })
            .WithUpcasters(UpcasterRegistry.Create()
                .Add(DeclareUpcaster
                    .For<UpcastEvolverOrderV2>(Slug)
                    .From<UpcastEvolverOrderV1>(1,
                        v1 => new UpcastEvolverOrderV2(v1.OrderId, v1.Amount, "ext-upcasted"))
                    .Build())
                .Build());

        var evolver = new UpcastEvolverOrderEvolver();
        string? observedRegion = null;

        // Act: use the serializer overload of DecideAndAppendAsync.
        var result = await eventStore.DecideAndAppendAsync(
            boundary: DcbQuery.ByTypes(Slug),
            evolver: evolver,
            decide: state =>
            {
                observedRegion = state.LastRegion;
                return Decision.Succeed();
            },
            toEventToPersist: _ => throw new InvalidOperationException("should not append"),
            serializer: serializer);

        // Assert: upcasted shape reached the evolver.
        result.IsSuccess.Should().BeTrue();
        observedRegion.Should().Be("ext-upcasted");
    }
}

// ---------------------------------------------------------------------------
// 4. Standalone backward-compatibility check
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that the public
/// <see cref="Evolver{TState}.Reconstitute(System.Collections.Generic.IEnumerable{Alberto.IEventEnvelope}, TState)"/>
/// and <see cref="Evolver{TState}.Evolve(TState, Alberto.IEventEnvelope)"/>
/// overloads still work without a serializer (raw JSON
/// fallback), so existing callers that invoke the evolver directly are unaffected.
/// </summary>
public sealed class EvolverPublicApiBackwardsCompatTests
{
    private static readonly string Slug = "upcast-evolver-order";

    [Fact]
    public void Reconstitute_StandaloneWithoutSerializer_DeserializesViaRawJson()
    {
        // This is the existing usage path (e.g. from EvolverTests.cs) — must still work.
        var orderId = Guid.NewGuid();
        var evolver = new UpcastEvolverOrderEvolver();

        var envelopes = new[]
        {
            MakeEnvelope(Slug, version: 2,
                json: $$"""{"OrderId":"{{orderId:D}}","Amount":1.0,"Region":"direct-region"}""")
        };

        var state = evolver.Reconstitute(envelopes);

        state.LastRegion.Should().Be("direct-region",
            "the public overload must still deserialize via raw JSON when called without a serializer");
    }

    private static IEventEnvelope MakeEnvelope(string slug, int version, string json)
        => new TestEnvelopeForUpcasting(slug, version, json);

    private sealed record TestEnvelopeForUpcasting(string Slug, int Version, string Json) : IEventEnvelope
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
}
