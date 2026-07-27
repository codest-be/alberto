using Alberto.Dcb.InMemory;
using Alberto.Dcb.Upcasting;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Dcb.Tests;

// ---------------------------------------------------------------------------
// Domain fixtures
//
// Convention (mirrors production use):
//   - Only the CURRENT (latest) version of an event type carries [EventType].
//   - Old versions used exclusively in upcast chains are plain records: no attribute.
// ---------------------------------------------------------------------------

// Used for EventType attribute / version reading tests.
[EventType("version-read-test", Version = 2)]
file record VersionReadTestV2(Guid OrderId, decimal Amount, string Region) : IEvent;

// Used for EventTypeAttribute default-version test.
[EventType("no-version-attr")]
file record EventWithNoVersionAttr(Guid Id) : IEvent;

// Used for VersionTagStamping tests — one separate ID per version so the scanner
// never sees duplicate IDs.
[EventType("stamp-test-v1")]
file record StampTestV1Event(Guid Id, decimal Amount) : IEvent;

[EventType("stamp-test-v2", Version = 2)]
file record StampTestV2Event(Guid Id, decimal Amount, string Region) : IEvent;

// Used for upcast-chain tests.
// v1 and v2 are OLD versions — no [EventType] attribute (not in the live registry).
file record UpcastOrderV1(Guid OrderId, decimal Amount);
file record UpcastOrderV2(Guid OrderId, decimal Amount, string Region);

[EventType("upcast-order", Version = 3)]
file record UpcastOrderV3(Guid OrderId, decimal Amount, string Region, string Currency) : IEvent;


// ---------------------------------------------------------------------------
// 1. EventType struct — equality and version semantics
// ---------------------------------------------------------------------------

public sealed class EventTypeVersionTests
{
    [Fact]
    public void SameId_DifferentVersion_AreEqual()
    {
        var v1 = new EventType("order-placed", 1);
        var v2 = new EventType("order-placed", 2);

        v1.Should().Be(v2);
        (v1 == v2).Should().BeTrue();
    }

    [Fact]
    public void SameId_DifferentVersion_HaveSameHashCode()
    {
        var v1 = new EventType("order-placed", 1);
        var v2 = new EventType("order-placed", 2);

        v1.GetHashCode().Should().Be(v2.GetHashCode());
    }

    [Fact]
    public void SameId_DifferentVersion_UsableAsEquivalentDictionaryKey()
    {
        var v1 = new EventType("order-placed", 1);
        var v2 = new EventType("order-placed", 2);

        var dict = new Dictionary<EventType, string> { [v1] = "found" };
        dict.TryGetValue(v2, out var value).Should().BeTrue();
        value.Should().Be("found");
    }

    [Fact]
    public void ToString_ReturnsId_NotVersion()
    {
        var et = new EventType("order-placed", 2);
        et.ToString().Should().Be("order-placed");
    }

    [Fact]
    public void ImplicitToString_ReturnsId_NotVersion()
    {
        var et = new EventType("order-placed", 2);
        string asString = et;
        asString.Should().Be("order-placed");
    }

    [Fact]
    public void FromType_ReadsVersionFromAttribute()
    {
        var et = EventType.FromType<VersionReadTestV2>();
        et.Id.Should().Be("version-read-test");
        et.Version.Should().Be(2);
    }

    [Fact]
    public void FromType_DefaultsToVersion1_WhenNoVersionSet()
    {
        var et = EventType.FromType<EventWithNoVersionAttr>();
        et.Version.Should().Be(1);
    }

    [Fact]
    public void EventTypeAttribute_Version_DefaultsTo1()
    {
        var attr = new EventTypeAttribute("my-event");
        attr.Version.Should().Be(1);
    }

    [Fact]
    public void EventTypeAttribute_Version_RejectsZeroOrNegative()
    {
        var attr = new EventTypeAttribute("my-event");
        var set0 = () => { attr.Version = 0; };
        var setNeg = () => { attr.Version = -1; };

        set0.Should().Throw<ArgumentOutOfRangeException>();
        setNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EventType_Constructor_DefaultsToVersion1()
    {
        var et = new EventType("order-placed");
        et.Version.Should().Be(1);
    }
}

// ---------------------------------------------------------------------------
// 2. Reserved tag concept enforcement
// ---------------------------------------------------------------------------

public sealed class ReservedTagConceptTests
{
    [Fact]
    public void EventTag_Constructor_Rejects_SchemaVersionConcept()
    {
        var act = () => new EventTag(EventTag.SchemaVersionConcept, "1");
        act.Should().Throw<ArgumentException>()
            .WithMessage($"*{EventTag.SchemaVersionConcept}*reserved*");
    }

    [Fact]
    public void EventTag_Parse_Rejects_SchemaVersionConcept()
    {
        var act = () => EventTag.Parse($"{EventTag.SchemaVersionConcept}:1");
        act.Should().Throw<ArgumentException>()
            .WithMessage($"*{EventTag.SchemaVersionConcept}*reserved*");
    }

    [Fact]
    public void TagAttribute_Rejects_SchemaVersionConcept()
    {
        var act = () => new TagAttribute(EventTag.SchemaVersionConcept);
        act.Should().Throw<ArgumentException>()
            .WithMessage($"*{EventTag.SchemaVersionConcept}*reserved*");
    }

    [Fact]
    public void EventTag_FromStorage_Allows_SchemaVersionConcept()
    {
        // The read-path bypass must not throw.
        var tag = EventTag.FromStorage(EventTag.SchemaVersionConcept, "2");
        tag.Concept.Should().Be(EventTag.SchemaVersionConcept);
        tag.Id.Should().Be("2");
    }
}

// ---------------------------------------------------------------------------
// 3. Version tag stamping and reading (InMemory backend)
// ---------------------------------------------------------------------------

public sealed class VersionTagStampingTests
{
    // Use FromRegistry with an explicit type map so we don't scan the whole test
    // assembly (which contains duplicate [EventType] IDs across test classes).
    private static readonly EventSerializer Serializer = EventSerializer.FromRegistry(
        new Dictionary<string, Type>
        {
            [EventTypeAttribute.GetEventTypeId(typeof(StampTestV1Event))] = typeof(StampTestV1Event),
            [EventTypeAttribute.GetEventTypeId(typeof(StampTestV2Event))] = typeof(StampTestV2Event),
        });

    [Fact]
    public void ExtractTags_V1Event_StampsV1Tag()
    {
        var evt = new StampTestV1Event(Guid.NewGuid(), 50m);
        var tags = Serializer.ExtractTags(evt);

        tags.Should().Contain(t =>
            t.Concept == EventTag.SchemaVersionConcept && t.Id == "1");
    }

    [Fact]
    public void ExtractTags_V2Event_StampsV2Tag()
    {
        var evt = new StampTestV2Event(Guid.NewGuid(), 50m, "us-east");
        var tags = Serializer.ExtractTags(evt);

        tags.Should().Contain(t =>
            t.Concept == EventTag.SchemaVersionConcept && t.Id == "2");
    }

    [Fact]
    public async Task AppendedEvent_Envelope_ContainsVersionTag()
    {
        var backend = new InMemoryEventStoreBackend();
        var evt = new StampTestV2Event(Guid.NewGuid(), 100m, "eu-west");
        var tags = Serializer.ExtractTags(evt);

        var persisted = await backend.AppendAsync(
            [new EventToPersist
            {
                EventType = EventType.FromType<StampTestV2Event>(),
                Tags = tags,
                EventData = Serializer.Serialize(evt)
            }],
            cancellationToken: TestContext.Current.CancellationToken);

        var envelope = persisted.Single();
        envelope.Tags.Should().Contain(t =>
            t.Concept == EventTag.SchemaVersionConcept && t.Id == "2",
            "version tag must survive the write→read round-trip");
    }

    [Fact]
    public async Task AppendedEvent_EventType_Version_MatchesAttribute()
    {
        var backend = new InMemoryEventStoreBackend();
        var evt = new StampTestV2Event(Guid.NewGuid(), 100m, "eu-west");
        var tags = Serializer.ExtractTags(evt);

        var persisted = await backend.AppendAsync(
            [new EventToPersist
            {
                EventType = EventType.FromType<StampTestV2Event>(),
                Tags = tags,
                EventData = Serializer.Serialize(evt)
            }],
            cancellationToken: TestContext.Current.CancellationToken);

        persisted.Single().EventType.Version.Should().Be(2);
    }

    [Fact]
    public async Task LegacyEvent_WithoutVersionTag_ReadsAsVersion1()
    {
        // Simulate a pre-versioning DB row: no _v tag in Tags,
        // EventType constructed without an explicit version (defaults to 1).
        var backend = new InMemoryEventStoreBackend();
        var legacy = new EventToPersist
        {
            EventType = new EventType("legacy-order"),   // no version → defaults to 1
            Tags = [],                                    // no _v tag
            EventData = """{"OrderId":"00000000-0000-0000-0000-000000000001","Amount":50}"""
        };

        var persisted = await backend.AppendAsync(
            [legacy],
            cancellationToken: TestContext.Current.CancellationToken);

        persisted.Single().EventType.Version.Should().Be(1,
            "events written before schema versioning was introduced must be treated as v1");
    }
}

// ---------------------------------------------------------------------------
// 4. ByAllTags / ByTypes boundary safety with version tags present (InMemory)
// ---------------------------------------------------------------------------

public sealed class VersionTagBoundaryTests
{
    private static IEventToPersist MakeEvent(string type, int version, string orderId)
    {
        var orderTag = EventTag.FromStorage("order", orderId);
        var versionTag = EventTag.ForVersion(version);
        return new EventToPersist
        {
            EventType = new EventType(type, version),
            Tags = [orderTag, versionTag],
            EventData = """{"test":true}"""
        };
    }

    [Fact]
    public async Task ByAllTags_StillMatchesBothVersions_WhenVersionTagPresent()
    {
        // Adding a _v tag to every event must NOT break existing ByAllTags queries.
        var backend = new InMemoryEventStoreBackend();
        var orderId = Guid.NewGuid().ToString("N")[..8];

        await backend.AppendAsync(
            [MakeEvent("order-placed", 1, orderId)],
            cancellationToken: TestContext.Current.CancellationToken);

        await backend.AppendAsync(
            [MakeEvent("order-placed", 2, orderId)],
            cancellationToken: TestContext.Current.CancellationToken);

        // Query uses ONLY the user-domain tag — version tag must not interfere.
        var query = DcbQuery.ByAllTags(EventTag.FromStorage("order", orderId));
        var results = await backend.StreamAsync(query, cancellationToken: TestContext.Current.CancellationToken);

        results.Should().HaveCount(2,
            "ByAllTags is containment-based: the extra _v tag must not break matching");
    }

    [Fact]
    public async Task ByTypes_MatchesV1AndV2_SameEventTypeId()
    {
        var backend = new InMemoryEventStoreBackend();
        var localId = Guid.NewGuid().ToString("N")[..8];

        await backend.AppendAsync(
            [MakeEvent("order-placed", 1, localId)],
            cancellationToken: TestContext.Current.CancellationToken);

        await backend.AppendAsync(
            [MakeEvent("order-placed", 2, localId)],
            cancellationToken: TestContext.Current.CancellationToken);

        // ByTypes filters by event_type column (Id only, no version).
        var query = DcbQuery.ByTypes(new EventType("order-placed"));
        var results = await backend.StreamAsync(query, cancellationToken: TestContext.Current.CancellationToken);

        results.Should().HaveCount(2,
            "ByTypes must match v1 and v2 of the same event type — version must not affect the filter key");
    }

    [Fact]
    public async Task ByTypes_WithVersionedEventType_StillMatchesBothVersions()
    {
        // Passing EventType("order-placed", 2) as a query filter must behave
        // identically to EventType("order-placed") because equality ignores Version.
        var backend = new InMemoryEventStoreBackend();
        var localId = Guid.NewGuid().ToString("N")[..8];

        await backend.AppendAsync(
            [MakeEvent("order-placed", 1, localId)],
            cancellationToken: TestContext.Current.CancellationToken);

        await backend.AppendAsync(
            [MakeEvent("order-placed", 2, localId)],
            cancellationToken: TestContext.Current.CancellationToken);

        var query = DcbQuery.ByTypes(new EventType("order-placed", 2));
        var results = await backend.StreamAsync(query, cancellationToken: TestContext.Current.CancellationToken);

        results.Should().HaveCount(2,
            "a versioned EventType used as a query filter must match all versions because Version is excluded from equality");
    }
}

// ---------------------------------------------------------------------------
// 5. DateTimeOffset round-trip (InMemory backend)
// ---------------------------------------------------------------------------

public sealed class DateTimeOffsetRoundTripTests
{
    [Fact]
    public async Task CreatedAt_IsDateTimeOffset_WithUtcOffset()
    {
        var fixedTime = new DateTimeOffset(2025, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var backend = new InMemoryEventStoreBackend(new FakeTimeProvider(fixedTime));

        var result = await backend.AppendAsync(
            [new EventToPersist
            {
                EventType = new EventType("test-event"),
                Tags = [],
                EventData = "{}"
            }],
            cancellationToken: TestContext.Current.CancellationToken);

        var envelope = result.Single();
        envelope.CreatedAt.Should().Be(fixedTime);
        envelope.CreatedAt.Offset.Should().Be(TimeSpan.Zero, "CreatedAt offset must be UTC");
    }

    [Fact]
    public void IEventEnvelope_CreatedAt_IsDateTimeOffset_CompileTimeCheck()
    {
        typeof(IEventEnvelope)
            .GetProperty(nameof(IEventEnvelope.CreatedAt))!
            .PropertyType
            .Should().Be(typeof(DateTimeOffset));
    }
}

// ---------------------------------------------------------------------------
// 6. Upcaster chain — happy path
// ---------------------------------------------------------------------------

public sealed class UpcasterChainTests
{
    // Build registry from explicit type list to avoid duplicate-ID conflicts in the test assembly.
    private static EventSerializer Serializer(UpcasterRegistry registry) =>
        EventSerializer.FromRegistry(
                new Dictionary<string, Type>
                {
                    [EventTypeAttribute.GetEventTypeId(typeof(UpcastOrderV3))] = typeof(UpcastOrderV3),
                })
            .WithUpcasters(registry);

    [Fact]
    public void SingleStep_V1ToV3_Upcast()
    {
        var registry = UpcasterRegistry.Create()
            .Add(DeclareUpcaster
                .For<UpcastOrderV3>("upcast-order")
                .From<UpcastOrderV1>(1, v1 => new UpcastOrderV3(v1.OrderId, v1.Amount, "default", "USD"))
                .Build())
            .Build();

        var s = Serializer(registry);
        var v1Json = """{"OrderId":"00000000-0000-0000-0000-000000000001","Amount":99.5}""";
        var envelope = V1Envelope("upcast-order", v1Json);

        var result = s.Deserialize(envelope);

        result.Should().BeOfType<UpcastOrderV3>();
        var v3 = (UpcastOrderV3)result;
        v3.Amount.Should().Be(99.5m);
        v3.Region.Should().Be("default");
        v3.Currency.Should().Be("USD");
    }

    [Fact]
    public void TwoStep_V1ToV3_Via_V2()
    {
        var registry = UpcasterRegistry.Create()
            .Add(DeclareUpcaster
                .For<UpcastOrderV3>("upcast-order")
                .From<UpcastOrderV1, UpcastOrderV2>(
                    1, v1 => new UpcastOrderV2(v1.OrderId, v1.Amount, "default-region"))
                .From<UpcastOrderV2>(
                    2, v2 => new UpcastOrderV3(v2.OrderId, v2.Amount, v2.Region, "EUR"))
                .Build())
            .Build();

        var s = Serializer(registry);
        var v1Json = """{"OrderId":"00000000-0000-0000-0000-000000000001","Amount":10}""";
        var envelope = V1Envelope("upcast-order", v1Json);

        var result = s.Deserialize(envelope);

        result.Should().BeOfType<UpcastOrderV3>();
        var v3 = (UpcastOrderV3)result;
        v3.Region.Should().Be("default-region");
        v3.Currency.Should().Be("EUR");
    }

    [Fact]
    public void TwoStep_V2Envelope_RunsOnlyStep2()
    {
        var registry = UpcasterRegistry.Create()
            .Add(DeclareUpcaster
                .For<UpcastOrderV3>("upcast-order")
                .From<UpcastOrderV1, UpcastOrderV2>(
                    1, _ => throw new Exception("step 1 must not execute for a v2 envelope"))
                .From<UpcastOrderV2>(
                    2, v2 => new UpcastOrderV3(v2.OrderId, v2.Amount, v2.Region, "GBP"))
                .Build())
            .Build();

        var s = Serializer(registry);
        var v2Json = """{"OrderId":"00000000-0000-0000-0000-000000000001","Amount":10,"Region":"eu-north"}""";
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid(), GlobalPosition = 1,
            EventType = new EventType("upcast-order", 2), Tags = [],
            EventData = v2Json, Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = s.Deserialize(envelope);

        result.Should().BeOfType<UpcastOrderV3>();
        ((UpcastOrderV3)result).Region.Should().Be("eu-north");
        ((UpcastOrderV3)result).Currency.Should().Be("GBP");
    }

    [Fact]
    public void CurrentVersionEnvelope_SkipsUpcasting()
    {
        // Registry covers v1 → v3; envelope is already at v3 → plain deserialize.
        var registry = UpcasterRegistry.Create()
            .Add(DeclareUpcaster
                .For<UpcastOrderV3>("upcast-order")
                .From<UpcastOrderV1, UpcastOrderV2>(1, v1 => new UpcastOrderV2(v1.OrderId, v1.Amount, "x"))
                .From<UpcastOrderV2>(2, v2 => new UpcastOrderV3(v2.OrderId, v2.Amount, v2.Region, "x"))
                .Build())
            .Build();

        var s = Serializer(registry);
        var v3Json = """{"OrderId":"00000000-0000-0000-0000-000000000001","Amount":5,"Region":"live","Currency":"JPY"}""";
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid(), GlobalPosition = 1,
            EventType = new EventType("upcast-order", 3), Tags = [],
            EventData = v3Json, Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = s.Deserialize(envelope);

        result.Should().BeOfType<UpcastOrderV3>();
        ((UpcastOrderV3)result).Currency.Should().Be("JPY",
            "plain deserialization must be used — upcaster must not run");
    }

    // ── Validation errors ───────────────────────────────────────────────────

    [Fact]
    public void Build_Throws_WhenNoSteps()
    {
        var act = () => DeclareUpcaster.For<UpcastOrderV3>("upcast-order").Build();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no steps*");
    }

    [Fact]
    public void Build_Throws_OnGap()
    {
        // v1 → v2 exists, v2 → v3 is missing, v3 → v4 exists.
        var act = () => DeclareUpcaster
            .For<UpcastOrderV3>("upcast-order")
            .From<UpcastOrderV1, UpcastOrderV2>(1, v1 => new UpcastOrderV2(v1.OrderId, v1.Amount, "x"))
            // intentional gap: fromVersion 2 is omitted
            .From<UpcastOrderV3>(3, _ => throw new Exception("unreachable"))
            .Build();

        // Actual message: "Upcaster for '...' has a gap between versions 1 and 3. Add a step for source version 2."
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*gap*1*and*3*");
    }

    [Fact]
    public void Build_Throws_OnDuplicateVersion_SignalingCyclicDefinition()
    {
        var act = () => DeclareUpcaster
            .For<UpcastOrderV3>("upcast-order")
            .From<UpcastOrderV1, UpcastOrderV2>(1, v1 => new UpcastOrderV2(v1.OrderId, v1.Amount, "x"))
            .From<UpcastOrderV1, UpcastOrderV2>(1, v1 => new UpcastOrderV2(v1.OrderId, v1.Amount, "y"))  // duplicate
            .Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate*1*");
    }

    [Fact]
    public void Apply_Throws_ClearError_WhenMissingLinkAtRuntime()
    {
        // Chain only covers v1 → v3.  An envelope claiming version 5 must produce a clear error.
        var registry = UpcasterRegistry.Create()
            .Add(DeclareUpcaster
                .For<UpcastOrderV3>("upcast-order")
                .From<UpcastOrderV1>(1, v1 => new UpcastOrderV3(v1.OrderId, v1.Amount, "x", "x"))
                .Build())
            .Build();

        var s = Serializer(registry);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid(), GlobalPosition = 1,
            EventType = new EventType("upcast-order", 5),   // no step for 5
            Tags = [], EventData = "{}",
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var act = () => s.Deserialize(envelope);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Upcaster*upcast-order*no step*5*");
    }

    [Fact]
    public void Registry_Throws_OnDuplicateEventTypeId()
    {
        var decl = DeclareUpcaster
            .For<UpcastOrderV3>("upcast-order")
            .From<UpcastOrderV1>(1, v1 => new UpcastOrderV3(v1.OrderId, v1.Amount, "x", "x"))
            .Build();

        var act = () => UpcasterRegistry.Create().Add(decl).Add(decl).Build();
        // Actual message: "An upcaster for event type 'upcast-order' is already registered."
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*upcast-order*already registered*");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static EventEnvelope V1Envelope(string eventTypeId, string json) =>
        new()
        {
            Id = Guid.NewGuid(), GlobalPosition = 1,
            EventType = new EventType(eventTypeId, 1), Tags = [],
            EventData = json, Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UtcNow
        };
}

// ---------------------------------------------------------------------------
// 7. EventSerializer.Deserialize respects the upcaster registry
// ---------------------------------------------------------------------------

public sealed class UpcasterSerializerDeserializeTests
{
    [Fact]
    public void Deserialize_WithUpcaster_ReturnsCurrentVersionType()
    {
        // Calls serializer.Deserialize directly (no evolver, no AlbertoStore, no CommandPipeline).
        // Verifies that the serializer respects the upcaster registry in isolation.
        var serializer = EventSerializer
            .FromRegistry(new Dictionary<string, Type>
            {
                [EventTypeAttribute.GetEventTypeId(typeof(UpcastOrderV3))] = typeof(UpcastOrderV3),
            })
            .WithUpcasters(UpcasterRegistry.Create()
                .Add(DeclareUpcaster
                    .For<UpcastOrderV3>("upcast-order")
                    .From<UpcastOrderV1>(
                        1, v1 => new UpcastOrderV3(v1.OrderId, v1.Amount, "command-path", "CAD"))
                    .Build())
                .Build());

        var oldEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid(), GlobalPosition = 1,
            EventType = new EventType("upcast-order", 1), Tags = [],
            EventData = """{"OrderId":"00000000-0000-0000-0000-000000000001","Amount":1}""",
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = serializer.Deserialize(oldEnvelope);

        result.Should().BeOfType<UpcastOrderV3>(
            "the upcaster must run on the command/decision read path");
        ((UpcastOrderV3)result).Region.Should().Be("command-path");
    }
}
