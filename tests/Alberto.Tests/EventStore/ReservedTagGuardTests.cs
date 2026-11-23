using System.Text.RegularExpressions;
using Xunit;

namespace Alberto.Tests;

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
        var exception = Assert.ThrowsAny<ArgumentException>(() => new EventTag(EventTag.SchemaVersionConcept, "1"));
        Assert.Equal("concept", exception.ParamName);
        Assert.Matches($"(?is){Regex.Escape(EventTag.SchemaVersionConcept)}.*reserved", exception.Message);
    }

    [Fact]
    public void PublicConstructor_AcceptsNormalConcept()
    {
        // Regression pin: the public constructor must not accidentally start
        // rejecting ordinary domain concepts.
        var tag = new EventTag("order", "42");
        Assert.Equal("order", tag.Concept);
        Assert.Equal("42", tag.Id);
        Assert.Equal("order:42", tag.Value);
    }

    // ------------------------------------------------------------------
    // Parse
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_RejectsReservedConcept()
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => EventTag.Parse($"{EventTag.SchemaVersionConcept}:1"));
        Assert.Matches($"(?is){Regex.Escape(EventTag.SchemaVersionConcept)}.*reserved", exception.Message);
    }

    [Fact]
    public void Parse_AcceptsNormalTag()
    {
        var tag = EventTag.Parse("customer:abc-123");
        Assert.Equal("customer", tag.Concept);
        Assert.Equal("abc-123", tag.Id);
    }

    // ------------------------------------------------------------------
    // TryParse  — this was the reported hole
    // ------------------------------------------------------------------

    [Fact]
    public void TryParse_ReturnsFalse_ForReservedConcept()
    {
        // Before the fix, EventTag.TryParse("_version:1", out _) returned true.
        var succeeded = EventTag.TryParse($"{EventTag.SchemaVersionConcept}:1", out var result);
        Assert.False(succeeded);
        Assert.Equal(default(EventTag), result);
    }

    [Theory]
    [InlineData("order:42", "order", "42")]
    [InlineData("customer:abc-123", "customer", "abc-123")]
    [InlineData("tenant_A:00000000-0000-0000-0000-000000000001", "tenant_A", "00000000-0000-0000-0000-000000000001")]
    public void TryParse_Succeeds_ForNormalTags(string input, string expectedConcept, string expectedId)
    {
        var succeeded = EventTag.TryParse(input, out var result);
        Assert.True(succeeded);
        Assert.Equal(expectedConcept, result.Concept);
        Assert.Equal(expectedId, result.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nocolon")]
    [InlineData("bad concept:1")]
    [InlineData("order:bad id")]
    public void TryParse_ReturnsFalse_ForMalformedInput(string input)
    {
        Assert.False(EventTag.TryParse(input, out _));
    }

    // ------------------------------------------------------------------
    // FromStorage (internal) — legitimate framework bypass
    //
    // FromStorage deliberately skips the reservation guard so the Postgres
    // backend and the in-memory backend can round-trip the _v tag that was
    // already written to storage.  The method is internal; these tests are
    // reachable because Alberto grants InternalsVisibleTo to this
    // test assembly.
    // ------------------------------------------------------------------

    [Fact]
    public void FromStorage_TwoPart_AllowsReservedConcept()
    {
        // The read hot-path must be able to materialise the _v tag without
        // going through the public reservation guard.
        var tag = EventTag.FromStorage(EventTag.SchemaVersionConcept, "3");
        Assert.Equal(EventTag.SchemaVersionConcept, tag.Concept);
        Assert.Equal("3", tag.Id);
        Assert.Equal("_version:3", tag.Value);
    }

    [Fact]
    public void FromStorage_SingleString_AllowsReservedConcept()
    {
        var tag = EventTag.FromStorage("_version:2");
        Assert.Equal(EventTag.SchemaVersionConcept, tag.Concept);
        Assert.Equal("2", tag.Id);
        Assert.Equal("_version:2", tag.Value);
    }

    [Fact]
    public void FromStorage_TwoPart_AllowsNormalConcept()
    {
        // Regression pin: the bypass path must also work for ordinary tags.
        var tag = EventTag.FromStorage("order", "99");
        Assert.Equal("order", tag.Concept);
        Assert.Equal("99", tag.Id);
    }

    [Fact]
    public void FromStorage_SingleString_AllowsNormalConcept()
    {
        var tag = EventTag.FromStorage("order:99");
        Assert.Equal("order", tag.Concept);
        Assert.Equal("99", tag.Id);
    }

    // ------------------------------------------------------------------
    // ForVersion (internal)
    // ------------------------------------------------------------------

    [Fact]
    public void ForVersion_ProducesCorrectTag()
    {
        var tag = EventTag.ForVersion(2);
        Assert.Equal(EventTag.SchemaVersionConcept, tag.Concept);
        Assert.Equal("2", tag.Id);
        Assert.Equal("_version:2", tag.Value);
    }

    [Fact]
    public void ForVersion_DefaultsToV1()
    {
        var tag = EventTag.ForVersion(1);
        Assert.Equal(EventTag.SchemaVersionConcept, tag.Concept);
        Assert.Equal("1", tag.Id);
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
        var exception = Assert.ThrowsAny<ArgumentException>(() => new TagAttribute(EventTag.SchemaVersionConcept));
        Assert.Equal("concept", exception.ParamName);
        Assert.Matches($"(?is){Regex.Escape(EventTag.SchemaVersionConcept)}.*reserved", exception.Message);
    }

    [Fact]
    public void TagAttribute_AcceptsNormalConcept()
    {
        var attr = new TagAttribute("order");
        Assert.Equal("order", attr.Concept);
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
        var exception = Assert.ThrowsAny<ArgumentException>(() => new EventTag(concept, "1"));
        Assert.Equal("concept", exception.ParamName);
        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsAnyLeadingUnderscoreConcept()
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => EventTag.Parse($"{Hypothetical}:1"));
        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_ReturnsFalse_ForAnyLeadingUnderscoreConcept()
    {
        Assert.False(EventTag.TryParse($"{Hypothetical}:1", out var result));
        Assert.Equal(default(EventTag), result);
    }

    [Fact]
    public void TagAttribute_RejectsAnyLeadingUnderscoreConcept()
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => new TagAttribute(Hypothetical));
        Assert.Equal("concept", exception.ParamName);
    }

    [Fact]
    public void An_underscore_elsewhere_in_the_concept_is_still_allowed()
    {
        // Only the leading position is reserved. Snake-case domain concepts such as
        // "order_line" must keep working — narrowing this would break real tags.
        var tag = new EventTag("order_line", "42");
        Assert.Equal("order_line:42", tag.Value);
    }

    [Fact]
    public void The_schema_version_concept_is_inside_the_reserved_prefix()
    {
        // If someone renames the version tag to something without the prefix, the guards
        // silently stop covering it. Pin the relationship rather than the literal name.
        Assert.StartsWith(EventTag.ReservedConceptPrefix, EventTag.SchemaVersionConcept);
    }
}
