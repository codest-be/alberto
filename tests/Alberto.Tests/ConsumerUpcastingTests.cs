using System.Text.Json;
using Alberto.InMemory;
using Alberto.Messaging;
using Alberto.Subscriptions;
using Alberto.Upcasting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests;

// ---------------------------------------------------------------------------
// Domain fixtures
//
// Convention (mirrors production use):
//   - Only the CURRENT (latest) version carries [EventType].
//   - Old versions used exclusively in upcast chains are plain records: no attribute.
// ---------------------------------------------------------------------------

// V1: no [EventType], lives only in upcast chains.
file record UpcastConsumerOrderV1(Guid OrderId, string? Region);

// V2: current version — adds Country which the upcaster fills in.
[EventType("upcast-consumer-order", Version = 2)]
file record UpcastConsumerOrderV2(Guid OrderId, string? Region, string? Country) : IEvent;

// External message type for the outbox mapping test.
[Message("consumer-order-msg", 1)]
file record ConsumerOrderMessage(string? Country);

// Projection state type. Non-positional record so the compiler generates
// the parameterless constructor required by the `where TState : new()` constraint.
file record ConsumerProjectionState
{
    public string? Country { get; init; }
}

// ---------------------------------------------------------------------------
// Shared test fixtures
// ---------------------------------------------------------------------------

file static class ConsumerUpcastFixtures
{
    public const string Slug = "upcast-consumer-order";

    /// <summary>
    /// Builds an EventSerializer that knows the V1→V2 upcaster for the test slug.
    /// The upcaster fills Country with "upcasted-country" so assertions can verify it ran.
    /// </summary>
    public static EventSerializer BuildSerializer() =>
        EventSerializer
            .FromRegistry(new Dictionary<string, Type>
            {
                [Slug] = typeof(UpcastConsumerOrderV2)
            })
            .WithUpcasters(UpcasterRegistry.Create()
                .Add(DeclareUpcaster
                    .For<UpcastConsumerOrderV2>(Slug)
                    .From<UpcastConsumerOrderV1>(
                        1,
                        v1 => new UpcastConsumerOrderV2(v1.OrderId, v1.Region, "upcasted-country"))
                    .Build())
                .Build());

    /// <summary>
    /// Builds a V1 envelope (no Country field in the JSON, EventType.Version = 1).
    /// A consumer that bypasses EventSerializer will see Country = null (raw JSON shape).
    /// A consumer that goes through EventSerializer will see Country = "upcasted-country".
    /// </summary>
    public static IEventEnvelope MakeV1Envelope(Guid orderId) => new EventEnvelope
    {
        Id = Guid.NewGuid(),
        GlobalPosition = 1,
        EventType = new EventType(Slug, 1),
        EventData = JsonSerializer.Serialize(new UpcastConsumerOrderV1(orderId, "us-east")),
        Tags = [],
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.UtcNow,
        TenantId = null,
    };
}

// ---------------------------------------------------------------------------
// (a) Declared async projection
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that the upcaster chain fires when <see cref="DeclaredAsyncProjection{TState}"/>
/// processes an event stored at an older schema version.
/// This test FAILS against the pre-fix code because <c>ProjectionEventHandler.ParseEvent</c>
/// called <c>envelope.ParseEvent&lt;TEvent&gt;()</c> — raw JSON — bypassing
/// <see cref="EventSerializer"/> and its upcaster chain.
/// </summary>
public sealed class DeclaredProjectionUpcastingTests
{
    [Fact]
    public async Task DeclaredAsyncProjection_UpcasterFires_BeforeHandlerSeesEvent()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var serializer = ConsumerUpcastFixtures.BuildSerializer();
        var envelope = ConsumerUpcastFixtures.MakeV1Envelope(orderId);

        var declaration = DeclareProjection.For<ConsumerProjectionState>("test-projection")
            .On<UpcastConsumerOrderV2>(
                id: e => e.OrderId.ToString(),
                apply: (_, e, _) => new ConsumerProjectionState { Country = e.Country })
            .Build();

        var stateStore = new InMemoryStateStore<ConsumerProjectionState>();

        // Pass serializer — the fix routes handler.ParseEvent through EventSerializer.
        var projection = new DeclaredAsyncProjection<ConsumerProjectionState>(
            declaration,
            _ => stateStore,
            serializer: serializer);

        // Act
        await projection.ProcessEventAsync(envelope);

        // Assert: the handler received the upcasted V2 shape.
        // If the fix is missing, Country is null (raw JSON shape has no Country field).
        var states = await stateStore.LoadManyAsync([orderId.ToString()], CancellationToken.None);
        states.Should().ContainKey(orderId.ToString());
        states[orderId.ToString()].Country.Should().Be("upcasted-country");
    }
}

// ---------------------------------------------------------------------------
// (b) Functional reactor (async)
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that the upcaster chain fires when <see cref="FunctionalReactor{TEvent}"/>
/// processes an event stored at an older schema version.
/// This test FAILS against the pre-fix code because <c>FunctionalReactor.ProcessAsync</c>
/// used <c>JsonSerializer.Deserialize&lt;TEvent&gt;</c> directly, bypassing
/// <see cref="EventSerializer"/> and its upcaster chain.
/// </summary>
public sealed class FunctionalReactorUpcastingTests
{
    [Fact]
    public async Task FunctionalReactor_UpcasterFires_BeforeHandlerSeesEvent()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var serializer = ConsumerUpcastFixtures.BuildSerializer();
        var envelope = ConsumerUpcastFixtures.MakeV1Envelope(orderId);

        string? observedCountry = null;

        // Pass serializer — the fix routes ProcessAsync through EventSerializer.
        var reactor = new FunctionalReactor<UpcastConsumerOrderV2>(
            "test-reactor",
            handler: (e, _, _) =>
            {
                observedCountry = e.Country;
                return Task.CompletedTask;
            },
            serializer: serializer);

        // Act
        await reactor.ProcessEventAsync(envelope);

        // Assert: the handler received the upcasted V2 shape.
        // If the fix is missing, Country is null (raw JSON shape has no Country field).
        observedCountry.Should().Be("upcasted-country");
    }
}

// ---------------------------------------------------------------------------
// (c) Sync reactor (runs during AppendAsync)
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that the upcaster chain fires when <see cref="SyncReactor{TEvent}"/>
/// processes an event stored at an older schema version.
/// This test FAILS against the pre-fix code because <c>SyncReactor.ProcessAsync</c>
/// used <c>JsonSerializer.Deserialize&lt;TEvent&gt;</c> directly, bypassing
/// <see cref="EventSerializer"/> and its upcaster chain.
/// </summary>
public sealed class SyncReactorUpcastingTests
{
    [Fact]
    public async Task SyncReactor_UpcasterFires_BeforeHandlerSeesEvent()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var serializer = ConsumerUpcastFixtures.BuildSerializer();
        var envelope = ConsumerUpcastFixtures.MakeV1Envelope(orderId);

        string? observedCountry = null;

        // Pass serializer — the fix routes ProcessAsync through EventSerializer.
        var reactor = new SyncReactor<UpcastConsumerOrderV2>(
            handler: (e, _, _) =>
            {
                observedCountry = e.Country;
                return Task.CompletedTask;
            },
            serializer: serializer);

        // Act
        await reactor.ProcessAsync([envelope], CancellationToken.None);

        // Assert: the handler received the upcasted V2 shape.
        // If the fix is missing, Country is null (raw JSON shape has no Country field).
        observedCountry.Should().Be("upcasted-country");
    }
}

// ---------------------------------------------------------------------------
// (d) Outbox message mapping
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that the upcaster chain fires when
/// <see cref="MessageMappingRegistryExtensions.Map{TEvent,TMessage}"/> maps an event
/// stored at an older schema version.
/// This test FAILS against the pre-fix code because the mapping closures used
/// <c>JsonSerializer.Deserialize&lt;TEvent&gt;</c> directly, bypassing
/// <see cref="EventSerializer"/> and its upcaster chain.
/// </summary>
public sealed class OutboxMessageMappingUpcastingTests
{
    [Fact]
    public async Task MessageMapping_UpcasterFires_BeforeMappingEvent()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var serializer = ConsumerUpcastFixtures.BuildSerializer();
        var envelope = ConsumerUpcastFixtures.MakeV1Envelope(orderId);

        var registry = new MessageMappingRegistry();
        registry.Map<UpcastConsumerOrderV2, ConsumerOrderMessage>(e => new ConsumerOrderMessage(e.Country));

        // The service provider exposes EventSerializer as an unkeyed singleton so the mapper
        // closure (which receives IServiceProvider at dispatch time) can resolve it.
        // In production, WithEventsFrom registers it — see DEFERRED EDIT in
        // AlbertoStoreBuilderExtensions.cs.
        var services = new ServiceCollection();
        services.AddSingleton(serializer);
        await using var sp = services.BuildServiceProvider();

        // Act
        var result = await registry.TryMapAsync(envelope, sp, CancellationToken.None);

        // Assert: the mapper received the upcasted V2 shape.
        // If the fix is missing, Country is null (raw JSON shape has no Country field).
        result.Should().NotBeNull();
        var mapped = JsonSerializer.Deserialize<ConsumerOrderMessage>(result!.Payload);
        mapped!.Country.Should().Be("upcasted-country");
    }
}
