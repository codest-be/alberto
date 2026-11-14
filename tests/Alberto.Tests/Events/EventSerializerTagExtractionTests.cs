using FluentAssertions;
using Xunit;

namespace Alberto.Tests;

// ---------------------------------------------------------------------------
// Domain fixtures for tag-extraction tests.
//
// Conventions (mirror the rest of the test suite):
//   - file-scoped records to avoid event-type ID collisions with other files.
//   - EventSerializer.FromRegistry with an explicit type map so we do not
//     scan the whole test assembly (which contains deliberate duplicate IDs).
// ---------------------------------------------------------------------------

// Event with a nullable tagged property to exercise the null-skip behaviour.
[EventType("tag-extract-nullable")]
file record NullableTagEvent(
    [property: Tag("order")] Guid? OrderId) : IEvent;

// Event whose tagged property is a Guid (must format with "D").
[EventType("tag-extract-guid")]
file record GuidTagEvent(
    [property: Tag("order")] Guid OrderId) : IEvent;

// Event whose tagged property is a non-Guid (must format with ToString()).
[EventType("tag-extract-string")]
file record StringTagEvent(
    [property: Tag("customer")] string CustomerId) : IEvent;

// Event with tagged properties for valueTransform tests.
[EventType("tag-extract-transform")]
file record TransformTagEvent(
    [property: Tag("order")] Guid OrderId,
    [property: Tag("customer")] string CustomerId) : IEvent;

// Event with NO tagged properties — should yield exactly one tag (_version:1).
[EventType("tag-extract-no-tags")]
file record NoTagsEvent(Guid Id, decimal Amount) : IEvent;

// Event with [EventType] but no explicit version — defaults to 1.
[EventType("tag-extract-default-version")]
file record DefaultVersionEvent(
    [property: Tag("order")] Guid OrderId) : IEvent;

// Event with an explicit version — should yield _version:3 as the last tag.
[EventType("tag-extract-explicit-version", Version = 3)]
file record ExplicitVersionEvent(
    [property: Tag("order")] Guid OrderId) : IEvent;

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class EventSerializerTagExtractionTests
{
    private static readonly EventSerializer Serializer = EventSerializer.FromRegistry(
        new Dictionary<string, Type>
        {
            ["tag-extract-nullable"]         = typeof(NullableTagEvent),
            ["tag-extract-guid"]             = typeof(GuidTagEvent),
            ["tag-extract-string"]           = typeof(StringTagEvent),
            ["tag-extract-transform"]        = typeof(TransformTagEvent),
            ["tag-extract-no-tags"]          = typeof(NoTagsEvent),
            ["tag-extract-default-version"]  = typeof(DefaultVersionEvent),
            ["tag-extract-explicit-version"] = typeof(ExplicitVersionEvent),
        });

    // ── 1. Null property values are skipped (no tag emitted) ────────────────

    [Fact]
    public void NullTaggedProperty_IsSkipped_AndNoTagEmitted()
    {
        var evt = new NullableTagEvent(OrderId: null);
        var tags = Serializer.ExtractTags(evt);

        tags.Should().NotContain(t => t.Concept == "order",
            "a null-valued [Tag] property must not produce an EventTag");
    }

    [Fact]
    public void NonNullTaggedProperty_IsNotSkipped()
    {
        var orderId = Guid.NewGuid();
        var evt = new NullableTagEvent(OrderId: orderId);
        var tags = Serializer.ExtractTags(evt);

        tags.Should().Contain(t => t.Concept == "order" && t.Id == orderId.ToString("D"));
    }

    // ── 2. Guid values are formatted with ToString("D") ─────────────────────

    [Fact]
    public void GuidTaggedProperty_FormattedWithD()
    {
        var id = Guid.NewGuid();
        var evt = new GuidTagEvent(OrderId: id);
        var tags = Serializer.ExtractTags(evt);

        // "D" format: 00000000-0000-0000-0000-000000000000 (lower-case, hyphens)
        tags.Should().Contain(t =>
            t.Concept == "order" && t.Id == id.ToString("D"),
            "Guid properties must be tagged with the 'D' format (dashes, lower-case)");
    }

    // ── 3. Non-Guid values are formatted with ToString() ────────────────────

    [Fact]
    public void StringTaggedProperty_FormattedWithToString()
    {
        var evt = new StringTagEvent(CustomerId: "cust-99");
        var tags = Serializer.ExtractTags(evt);

        tags.Should().Contain(t => t.Concept == "customer" && t.Id == "cust-99",
            "non-Guid string properties must be tagged using their raw ToString() value");
    }

    // ── 4. valueTransform callback receives (concept, rawValue) ─────────────

    [Fact]
    public void ValueTransform_ReceivesConceptAndRawValue_AndItsReturnValueBecomesTagId()
    {
        var orderId = Guid.NewGuid();
        var received = new List<(string Concept, string RawValue)>();

        var evt = new TransformTagEvent(OrderId: orderId, CustomerId: "cust-1");
        var tags = Serializer.ExtractTags(evt, (concept, raw) =>
        {
            received.Add((concept, raw));
            return "transformed-" + raw;
        });

        received.Should().Contain(("order", orderId.ToString("D")),
            "the Guid rawValue passed to valueTransform must use the 'D' format");
        received.Should().Contain(("customer", "cust-1"));

        tags.Should().Contain(t => t.Concept == "order" && t.Id == $"transformed-{orderId:D}");
        tags.Should().Contain(t => t.Concept == "customer" && t.Id == "transformed-cust-1");
    }

    // ── 5. _version:N tag is the LAST element ───────────────────────────────

    [Fact]
    public void VersionTag_IsLastElement_WhenTaggedPropertiesPresent()
    {
        var evt = new GuidTagEvent(OrderId: Guid.NewGuid());
        var tags = Serializer.ExtractTags(evt).ToList();

        tags.Last().Concept.Should().Be(EventTag.SchemaVersionConcept,
            "the _version tag must be the last tag in the returned collection");
    }

    [Fact]
    public void VersionTag_IsLastElement_WhenNoTaggedProperties()
    {
        var evt = new NoTagsEvent(Id: Guid.NewGuid(), Amount: 99m);
        var tags = Serializer.ExtractTags(evt).ToList();

        tags.Last().Concept.Should().Be(EventTag.SchemaVersionConcept,
            "even when there are no [Tag] properties the _version tag must be last");
    }

    // ── 6. Type with no [Tag] properties yields exactly one tag ─────────────

    [Fact]
    public void TypeWithNoTaggedProperties_YieldsExactlyOneTag_TheVersionTag()
    {
        var evt = new NoTagsEvent(Id: Guid.NewGuid(), Amount: 42m);
        var tags = Serializer.ExtractTags(evt);

        tags.Should().HaveCount(1,
            "a type with zero [Tag] properties must produce exactly the one reserved _version tag");
        tags.Single().Concept.Should().Be(EventTag.SchemaVersionConcept);
        tags.Single().Id.Should().Be("1");
    }

    // ── 7. Version defaults to 1 when [EventType] has no explicit Version ───

    [Fact]
    public void Version_DefaultsToOne_WhenEventTypeAttributeHasNoVersion()
    {
        var evt = new DefaultVersionEvent(OrderId: Guid.NewGuid());
        var tags = Serializer.ExtractTags(evt);

        tags.Should().Contain(t =>
            t.Concept == EventTag.SchemaVersionConcept && t.Id == "1",
            "when [EventType] carries no explicit Version the stamp must be _version:1");
    }

    [Fact]
    public void Version_UsesExplicitVersionFromAttribute()
    {
        var evt = new ExplicitVersionEvent(OrderId: Guid.NewGuid());
        var tags = Serializer.ExtractTags(evt);

        tags.Should().Contain(t =>
            t.Concept == EventTag.SchemaVersionConcept && t.Id == "3",
            "when [EventType(Version = 3)] the stamp must be _version:3");
    }
}
