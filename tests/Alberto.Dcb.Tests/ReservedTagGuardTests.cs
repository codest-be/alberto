using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests;

// ---------------------------------------------------------------------------
// Reserved-concept guard contract
//
// Invariant: the "_version" concept is reserved for the framework's schema-version
// tag.  Every public construction path MUST reject it; every internal bypass
// (FromStorage, ForVersion) MUST accept it without corruption.
//
// These tests pin the entire surface so a regression cannot go unnoticed.
// ---------------------------------------------------------------------------

/// <summary>
/// Guards the <see cref="EventTag.SchemaVersionConcept"/> reservation across
/// every construction path into <see cref="EventTag"/>.
/// </summary>
public sealed class EventTagReservedConceptGuardTests
{
    // ------------------------------------------------------------------
    // Public constructor
    // ------------------------------------------------------------------

    [Fact]
    public void PublicConstructor_RejectsReservedConcept()
    {
        var act = () => new EventTag(EventTag.SchemaVersionConcept, "1");
        act.Should().Throw<ArgumentException>()
           .WithParameterName("concept")
           .WithMessage($"*{EventTag.SchemaVersionConcept}*reserved*");
    }

    [Fact]
    public void PublicConstructor_AcceptsNormalConcept()
    {
        // Regression pin: the public constructor must not accidentally start
        // rejecting ordinary domain concepts.
        var tag = new EventTag("order", "42");
        tag.Concept.Should().Be("order");
        tag.Id.Should().Be("42");
        tag.Value.Should().Be("order:42");
    }

    // ------------------------------------------------------------------
    // Parse
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_RejectsReservedConcept()
    {
        var act = () => EventTag.Parse($"{EventTag.SchemaVersionConcept}:1");
        act.Should().Throw<ArgumentException>()
           .WithMessage($"*{EventTag.SchemaVersionConcept}*reserved*");
    }

    [Fact]
    public void Parse_AcceptsNormalTag()
    {
        var tag = EventTag.Parse("customer:abc-123");
        tag.Concept.Should().Be("customer");
        tag.Id.Should().Be("abc-123");
    }

    // ------------------------------------------------------------------
    // TryParse  — this was the reported hole
    // ------------------------------------------------------------------

    [Fact]
    public void TryParse_ReturnsFalse_ForReservedConcept()
    {
        // Before the fix, EventTag.TryParse("_version:1", out _) returned true.
        var succeeded = EventTag.TryParse($"{EventTag.SchemaVersionConcept}:1", out var result);
        succeeded.Should().BeFalse();
        result.Should().Be(default(EventTag));
    }

    [Theory]
    [InlineData("order:42", "order", "42")]
    [InlineData("customer:abc-123", "customer", "abc-123")]
    [InlineData("tenant_A:00000000-0000-0000-0000-000000000001", "tenant_A", "00000000-0000-0000-0000-000000000001")]
    public void TryParse_Succeeds_ForNormalTags(string input, string expectedConcept, string expectedId)
    {
        var succeeded = EventTag.TryParse(input, out var result);
        succeeded.Should().BeTrue();
        result.Concept.Should().Be(expectedConcept);
        result.Id.Should().Be(expectedId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nocolon")]
    [InlineData("bad concept:1")]
    [InlineData("order:bad id")]
    public void TryParse_ReturnsFalse_ForMalformedInput(string input)
    {
        EventTag.TryParse(input, out _).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // FromStorage (internal) — legitimate framework bypass
    //
    // FromStorage deliberately skips the reservation guard so the Postgres
    // backend and the in-memory backend can round-trip the _v tag that was
    // already written to storage.  The method is internal; these tests are
    // reachable because Alberto.Dcb grants InternalsVisibleTo to this
    // test assembly.
    // ------------------------------------------------------------------

    [Fact]
    public void FromStorage_TwoPart_AllowsReservedConcept()
    {
        // The read hot-path must be able to materialise the _v tag without
        // going through the public reservation guard.
        var tag = EventTag.FromStorage(EventTag.SchemaVersionConcept, "3");
        tag.Concept.Should().Be(EventTag.SchemaVersionConcept);
        tag.Id.Should().Be("3");
        tag.Value.Should().Be("_version:3");
    }

    [Fact]
    public void FromStorage_SingleString_AllowsReservedConcept()
    {
        var tag = EventTag.FromStorage("_version:2");
        tag.Concept.Should().Be(EventTag.SchemaVersionConcept);
        tag.Id.Should().Be("2");
        tag.Value.Should().Be("_version:2");
    }

    [Fact]
    public void FromStorage_TwoPart_AllowsNormalConcept()
    {
        // Regression pin: the bypass path must also work for ordinary tags.
        var tag = EventTag.FromStorage("order", "99");
        tag.Concept.Should().Be("order");
        tag.Id.Should().Be("99");
    }

    [Fact]
    public void FromStorage_SingleString_AllowsNormalConcept()
    {
        var tag = EventTag.FromStorage("order:99");
        tag.Concept.Should().Be("order");
        tag.Id.Should().Be("99");
    }

    // ------------------------------------------------------------------
    // ForVersion (internal)
    // ------------------------------------------------------------------

    [Fact]
    public void ForVersion_ProducesCorrectTag()
    {
        var tag = EventTag.ForVersion(2);
        tag.Concept.Should().Be(EventTag.SchemaVersionConcept);
        tag.Id.Should().Be("2");
        tag.Value.Should().Be("_version:2");
    }

    [Fact]
    public void ForVersion_DefaultsToV1()
    {
        var tag = EventTag.ForVersion(1);
        tag.Concept.Should().Be(EventTag.SchemaVersionConcept);
        tag.Id.Should().Be("1");
    }
}

/// <summary>
/// Guards the <see cref="EventTag.SchemaVersionConcept"/> reservation in
/// <see cref="TagAttribute"/>.
/// </summary>
public sealed class TagAttributeReservedConceptGuardTests
{
    [Fact]
    public void TagAttribute_RejectsReservedConcept()
    {
        var act = () => new TagAttribute(EventTag.SchemaVersionConcept);
        act.Should().Throw<ArgumentException>()
           .WithParameterName("concept")
           .WithMessage($"*{EventTag.SchemaVersionConcept}*reserved*");
    }

    [Fact]
    public void TagAttribute_AcceptsNormalConcept()
    {
        var attr = new TagAttribute("order");
        attr.Concept.Should().Be("order");
    }
}

/// <summary>
/// Guards the <see cref="EventTag.SchemaVersionConcept"/> reservation across
/// every construction path into <see cref="TagPattern"/>.
/// </summary>
public sealed class TagPatternReservedConceptGuardTests
{
    // ------------------------------------------------------------------
    // Parse
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_ExactForm_RejectsReservedConcept()
    {
        var act = () => TagPattern.Parse($"{EventTag.SchemaVersionConcept}:1");
        act.Should().Throw<ArgumentException>()
           .WithMessage($"*{EventTag.SchemaVersionConcept}*reserved*");
    }

    [Fact]
    public void Parse_WildcardForm_RejectsReservedConcept()
    {
        var act = () => TagPattern.Parse($"{EventTag.SchemaVersionConcept}:*");
        act.Should().Throw<ArgumentException>()
           .WithMessage($"*{EventTag.SchemaVersionConcept}*reserved*");
    }

    // ------------------------------------------------------------------
    // TryParse
    // ------------------------------------------------------------------

    [Fact]
    public void TryParse_ReturnsFalse_ForReservedConcept_ExactForm()
    {
        TagPattern.TryParse($"{EventTag.SchemaVersionConcept}:1", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_ReturnsFalse_ForReservedConcept_WildcardForm()
    {
        TagPattern.TryParse($"{EventTag.SchemaVersionConcept}:*", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Succeeds_ForNormalExactPattern()
    {
        var succeeded = TagPattern.TryParse("order:42", out var result);
        succeeded.Should().BeTrue();
        result.Concept.Should().Be("order");
        result.Id.Should().Be("42");
    }

    [Fact]
    public void TryParse_Succeeds_ForNormalWildcardPattern()
    {
        var succeeded = TagPattern.TryParse("order:*", out var result);
        succeeded.Should().BeTrue();
        result.IsWildcard.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Prefix / Exact factory methods
    // ------------------------------------------------------------------

    [Fact]
    public void Prefix_RejectsReservedConcept()
    {
        var act = () => TagPattern.Prefix(EventTag.SchemaVersionConcept);
        act.Should().Throw<ArgumentException>()
           .WithMessage($"*{EventTag.SchemaVersionConcept}*reserved*");
    }

    [Fact]
    public void Exact_StringOverload_RejectsReservedConcept()
    {
        var act = () => TagPattern.Exact(EventTag.SchemaVersionConcept, "1");
        act.Should().Throw<ArgumentException>()
           .WithMessage($"*{EventTag.SchemaVersionConcept}*reserved*");
    }

    [Fact]
    public void Exact_EventTagOverload_AcceptsNormalTag()
    {
        // A normal EventTag can always be promoted to an exact TagPattern.
        // This is a regression pin — the implicit conversion should keep working.
        var tag = new EventTag("order", "99");
        var pattern = TagPattern.Exact(tag);
        pattern.Concept.Should().Be("order");
        pattern.Id.Should().Be("99");
        pattern.IsExact.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Implicit conversion from EventTag
    // ------------------------------------------------------------------

    [Fact]
    public void ImplicitConversion_FromNormalEventTag_CreatesExactPattern()
    {
        var tag = new EventTag("customer", "7");
        TagPattern pattern = tag;
        pattern.IsExact.Should().BeTrue();
        pattern.Concept.Should().Be("customer");
    }
}

// ---------------------------------------------------------------------------
// Prefix-wide reservation
//
// The reservation covers the whole "_" prefix, not just the concept names
// Alberto happens to write today. That is what makes it safe to introduce a
// second framework tag after the API freezes: the space it lands in was never
// reachable from application code, so nobody's tag can collide with it.
//
// These tests use concepts Alberto does NOT use, so they fail if the guards
// ever narrow back to exact-matching SchemaVersionConcept.
// ---------------------------------------------------------------------------

public sealed class ReservedConceptPrefixTests
{
    // Names Alberto does not use — only the prefix rule can reject these.
    private const string Hypothetical = "_causation";
    private const string BareUnderscore = "_";

    [Theory]
    [InlineData(Hypothetical)]
    [InlineData(BareUnderscore)]
    [InlineData("_v")]      // the previous name for the version tag; still reserved
    public void PublicConstructor_RejectsAnyLeadingUnderscoreConcept(string concept)
    {
        var act = () => new EventTag(concept, "1");
        act.Should().Throw<ArgumentException>()
           .WithParameterName("concept")
           .WithMessage("*reserved*");
    }

    [Fact]
    public void Parse_RejectsAnyLeadingUnderscoreConcept()
    {
        var act = () => EventTag.Parse($"{Hypothetical}:1");
        act.Should().Throw<ArgumentException>().WithMessage("*reserved*");
    }

    [Fact]
    public void TryParse_ReturnsFalse_ForAnyLeadingUnderscoreConcept()
    {
        EventTag.TryParse($"{Hypothetical}:1", out var result).Should().BeFalse();
        result.Should().Be(default(EventTag));
    }

    [Fact]
    public void TagAttribute_RejectsAnyLeadingUnderscoreConcept()
    {
        var act = () => new TagAttribute(Hypothetical);
        act.Should().Throw<ArgumentException>().WithParameterName("concept");
    }

    [Theory]
    [InlineData(Hypothetical)]
    [InlineData(BareUnderscore)]
    public void TagPattern_factories_reject_any_leading_underscore_concept(string concept)
    {
        var exact = () => TagPattern.Exact(concept, "1");
        exact.Should().Throw<ArgumentException>().WithParameterName("concept");

        var prefix = () => TagPattern.Prefix(concept);
        prefix.Should().Throw<ArgumentException>().WithParameterName("concept");

        var parse = () => TagPattern.Parse($"{concept}:*");
        parse.Should().Throw<ArgumentException>();

        TagPattern.TryParse($"{concept}:*", out _).Should().BeFalse();
    }

    [Fact]
    public void An_underscore_elsewhere_in_the_concept_is_still_allowed()
    {
        // Only the leading position is reserved. Snake-case domain concepts such as
        // "order_line" must keep working — narrowing this would break real tags.
        var tag = new EventTag("order_line", "42");
        tag.Value.Should().Be("order_line:42");

        TagPattern.Exact("order_line", "42").Concept.Should().Be("order_line");
    }

    [Fact]
    public void The_schema_version_concept_is_inside_the_reserved_prefix()
    {
        // If someone renames the version tag to something without the prefix, the guards
        // silently stop covering it. Pin the relationship rather than the literal name.
        EventTag.SchemaVersionConcept.Should().StartWith(EventTag.ReservedConceptPrefix);
    }
}
