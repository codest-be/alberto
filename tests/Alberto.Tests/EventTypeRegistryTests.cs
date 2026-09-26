using System.Text.Json;
using System.Text.Json.Serialization;
using Alberto.Commands;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Tests.SampleEvents;
using Alberto.Upcasting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alberto.Tests;

// ---------------------------------------------------------------------------
// IEventTypeRegistry (#178): the reflection scan and a hand-written registry must describe the
// same event types identically, because a trimmed or Native AOT application swaps the first for
// the second and expects nothing else to change. The parity test is the contract; the rest pin
// down the builder, WithEvents and the serializer over a registry.
//
// Alberto.Tests.SampleEvents is the scanned assembly, for the reason given in its Events.cs.
// ---------------------------------------------------------------------------

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VersionedOrder))]
[JsonSerializable(typeof(VersionedOrderV1))]
[JsonSerializable(typeof(PlainNote))]
internal sealed partial class SampleEventsJsonContext : JsonSerializerContext;

public class EventTypeRegistryTests
{
    private const string ModuleKey = "registry_tests";

    /// <summary>What an AOT application writes by hand for Alberto.Tests.SampleEvents.</summary>
    private static IEventTypeRegistry HandBuilt() => EventTypeRegistry.CreateBuilder()
        .Add(SampleEventsJsonContext.Default.VersionedOrder,
            e => [new("order", ((VersionedOrder)e).OrderId)])
        .Add(SampleEventsJsonContext.Default.PlainNote,
            e => [new("note", ((PlainNote)e).NoteId)])
        .Build();

    private static IEventTypeRegistry Scanned() =>
        EventTypeRegistry.FromAssembly(typeof(VersionedOrder).Assembly);

    private static readonly IEvent[] Samples =
    [
        new VersionedOrder(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), 12.5m, "BE"),
        new PlainNote(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"), "hello"),
    ];

    [Fact]
    public void Hand_built_registry_describes_the_sample_assembly_exactly_as_the_scan_does()
    {
        var scanned = Scanned().Descriptors.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        var hand = HandBuilt().Descriptors.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();

        hand.Select(Facts).Should().Equal(scanned.Select(Facts),
            "a hand-written registry must be a drop-in for the scan: same ids, versions, " +
            "opt-outs, CLR types and JSON contract types");
    }

    [Fact]
    public void Hand_built_and_scanned_serializers_agree_on_tags_json_and_event_type()
    {
        var scanned = EventSerializer.FromAssembly(typeof(VersionedOrder).Assembly);
        var hand = EventSerializer.FromRegistry(HandBuilt());

        foreach (var sample in Samples)
        {
            hand.ExtractTags(sample).Should().Equal(scanned.ExtractTags(sample),
                $"tags decide which boundary {sample.GetType().Name} falls into");
            hand.Serialize(sample).Should().Be(scanned.Serialize(sample));
            hand.GetEventType(sample).Should().Be(scanned.GetEventType(sample));
            hand.GetEventType(sample).Version.Should().Be(scanned.GetEventType(sample).Version);
        }
    }

    [Fact]
    public void Hand_built_and_scanned_serializers_read_each_others_payloads()
    {
        var scanned = EventSerializer.FromAssembly(typeof(VersionedOrder).Assembly);
        var hand = EventSerializer.FromRegistry(HandBuilt());

        foreach (var sample in Samples)
        {
            var envelope = Stored(scanned.GetEventType(sample), scanned.Serialize(sample));
            hand.Deserialize(envelope).Should().Be(scanned.Deserialize(envelope));
            hand.Deserialize(envelope).Should().Be(sample);
        }
    }

    [Fact]
    public void Serializer_over_a_hand_built_registry_upcasts_through_the_contexts_old_shape()
    {
        var orderId = Guid.NewGuid();
        var serializer = EventSerializer.FromRegistry(HandBuilt())
            .WithUpcasters(UpcasterRegistry.Create()
                .Add(DeclareUpcaster.For<VersionedOrder>("e2e-versioned-order")
                    .From<VersionedOrderV1>(1, v1 => new VersionedOrder(v1.OrderId, v1.Amount, "unknown"))
                    .Build())
                .Build());

        var read = serializer.Deserialize(Stored(
            new EventType("e2e-versioned-order", 1),
            JsonSerializer.Serialize(new { OrderId = orderId, Amount = 3m })));

        read.Should().Be(new VersionedOrder(orderId, 3m, "unknown"));
    }

    [Fact]
    public void Serializer_over_a_hand_built_registry_refuses_an_unregistered_event_type()
    {
        var serializer = EventSerializer.FromRegistry(HandBuilt());

        var act = () => serializer.Serialize(new UnregisteredEvent("x"));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{nameof(UnregisteredEvent)}*not registered*");
    }

    [Fact]
    public void Serializer_over_a_hand_built_registry_refuses_an_unknown_id_on_read()
    {
        var serializer = EventSerializer.FromRegistry(HandBuilt());

        var act = () => serializer.Deserialize(Stored(new EventType("nope"), "{}"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*No registered type for event 'nope'*");
    }

    [Fact]
    public void Scanned_serializer_still_writes_an_event_type_its_scan_did_not_include()
    {
        // The pre-registry behaviour, kept for the reflection path: WithEventsFrom has always
        // written an unregistered type by runtime type, and CurioStack relies on nothing changing.
        var serializer = EventSerializer.FromAssembly(typeof(VersionedOrder).Assembly);

        serializer.Serialize(new UnregisteredEvent("x")).Should().Contain("\"Name\":\"x\"");
        serializer.ExtractTags(new UnregisteredEvent("x")).Should().Contain(new EventTag("thing", "x"));
    }

    [Fact]
    public void Builder_rejects_a_duplicate_id()
    {
        var act = () => EventTypeRegistry.CreateBuilder()
            .Add(SampleEventsJsonContext.Default.PlainNote)
            .Add("e2e-plain-note", SampleEventsJsonContext.Default.VersionedOrder)
            .Build();

        act.Should().Throw<ArgumentException>().WithMessage("*'e2e-plain-note'*unique*");
    }

    [Fact]
    public void Builder_rejects_one_type_under_two_ids()
    {
        var act = () => EventTypeRegistry.CreateBuilder()
            .Add(SampleEventsJsonContext.Default.PlainNote)
            .Add("another-note", SampleEventsJsonContext.Default.PlainNote)
            .Build();

        act.Should().Throw<ArgumentException>().WithMessage("*exactly one event type id*");
    }

    [Fact]
    public void Builder_requires_EventType_attribute_unless_the_id_is_explicit()
    {
        var context = new UnregisteredJsonContext();

        var withoutId = () => EventTypeRegistry.CreateBuilder().Add(context.UnregisteredEvent);
        withoutId.Should().Throw<InvalidOperationException>().WithMessage("*does not have an [EventType] attribute*");

        var withId = EventTypeRegistry.CreateBuilder()
            .Add("unregistered", context.UnregisteredEvent, version: 3, upcastingNotRequired: true)
            .Build();
        withId.TryGetById("unregistered", out var d).Should().BeTrue();
        (d!.Version, d.UpcastingNotRequired, d.ClrType).Should().Be((3, true, typeof(UnregisteredEvent)));
    }

    [Fact]
    public void Descriptor_rejects_a_contract_for_a_type_that_is_not_an_event()
    {
        var act = () => new EventTypeDescriptor(
            "not-an-event", 1, false, SampleEventsJsonContext.Default.VersionedOrderV1, _ => []);

        act.Should().Throw<ArgumentException>().WithMessage($"*{nameof(VersionedOrderV1)}*{nameof(IEvent)}*");
    }

    [Fact]
    public void WithEvents_registers_a_serializer_over_the_registry_with_the_modules_upcasters()
    {
        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, builder => builder
            .WithInMemory()
            .WithEvents(HandBuilt())
            .AddUpcaster(DeclareUpcaster.For<VersionedOrder>("e2e-versioned-order")
                .From<VersionedOrderV1>(1, v1 => new VersionedOrder(v1.OrderId, v1.Amount, "unknown"))
                .Build()));
        using var sp = services.BuildServiceProvider();

        // Startup validation reads the registry's descriptors: the upcaster covers Version = 2.
        var validate = () => sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(ModuleKey);
        validate.Should().NotThrow();

        var serializer = sp.GetRequiredKeyedService<EventSerializer>(ModuleKey);
        var orderId = Guid.NewGuid();
        serializer.Deserialize(Stored(
                new EventType("e2e-versioned-order", 1),
                JsonSerializer.Serialize(new { OrderId = orderId, Amount = 1m })))
            .Should().Be(new VersionedOrder(orderId, 1m, "unknown"));

        sp.GetRequiredKeyedService<AlbertoStore>(ModuleKey).Should().NotBeNull();
    }

    [Fact]
    public void WithEvents_feeds_startup_validation_so_a_missing_upcaster_is_still_ALB0018()
    {
        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, builder => builder.WithInMemory().WithEvents(HandBuilt()));
        using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(ModuleKey);

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch("*ALB0018*");
    }

    [Fact]
    public void Reading_without_a_serializer_uses_the_serializers_case_insensitive_default()
    {
        // The no-serializer fallback used to read with System.Text.Json's own (case-sensitive)
        // defaults while every serializer read case-insensitively, so one payload could come out
        // of the two paths differently. They now agree.
        var noteId = Guid.NewGuid();
        var envelope = Stored(new EventType("e2e-plain-note"), $$"""{"noteid":"{{noteId}}","text":"lower"}""");

        var viaFallback = EventEnvelopeExtensions.DeserializeEvent<PlainNote>(envelope, serializer: null);
        var viaSerializer = EventSerializer.FromRegistry(HandBuilt()).Deserialize(envelope);

        viaFallback.Should().Be(new PlainNote(noteId, "lower"));
        viaFallback.Should().Be(viaSerializer);
    }

    private static (string, int, bool, Type, Type) Facts(EventTypeDescriptor d) =>
        (d.Id, d.Version, d.UpcastingNotRequired, d.ClrType, d.JsonTypeInfo.Type);

    private static IEventEnvelope Stored(EventType type, string json) => new EventEnvelope
    {
        Id = Guid.NewGuid(),
        GlobalPosition = 1,
        EventType = type,
        Tags = [],
        EventData = json,
        Metadata = new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.UtcNow,
    };
}

/// <summary>An event with no <see cref="EventTypeAttribute"/>, as a scan would skip it.</summary>
public sealed record UnregisteredEvent([property: Tag("thing")] string Name) : IEvent;

[JsonSerializable(typeof(UnregisteredEvent))]
internal sealed partial class UnregisteredJsonContext : JsonSerializerContext;
