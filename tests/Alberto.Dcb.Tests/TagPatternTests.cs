using Xunit;

namespace Alberto.Dcb.Tests;

public class TagPatternTests
{
    #region Parse Tests

    [Theory]
    [InlineData("order:123", "order", "123", false)]
    [InlineData("customer:abc-def", "customer", "abc-def", false)]
    [InlineData("product:XYZ_001", "product", "XYZ_001", false)]
    public void Parse_ExactPattern_ShouldCreateExactPattern(string input, string expectedConcept, string expectedId, bool expectedWildcard)
    {
        var pattern = TagPattern.Parse(input);

        Assert.Equal(expectedConcept, pattern.Concept);
        Assert.Equal(expectedId, pattern.Id);
        Assert.Equal(expectedWildcard, pattern.IsWildcard);
        Assert.True(pattern.IsExact);
    }

    [Theory]
    [InlineData("order:*", "order")]
    [InlineData("customer:*", "customer")]
    [InlineData("very-long-concept-name:*", "very-long-concept-name")]
    public void Parse_WildcardPattern_ShouldCreateWildcardPattern(string input, string expectedConcept)
    {
        var pattern = TagPattern.Parse(input);

        Assert.Equal(expectedConcept, pattern.Concept);
        Assert.Null(pattern.Id);
        Assert.True(pattern.IsWildcard);
        Assert.False(pattern.IsExact);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nocolon")]
    [InlineData(":noConcept")]
    [InlineData("concept:")]
    public void Parse_InvalidPattern_ShouldThrow(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => TagPattern.Parse(input));
    }

    [Theory]
    [InlineData("invalid@concept:123")]
    [InlineData("concept:invalid@id")]
    [InlineData("con cept:123")]
    public void Parse_InvalidCharacters_ShouldThrow(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => TagPattern.Parse(input));
    }

    #endregion

    #region TryParse Tests

    [Theory]
    [InlineData("order:123", true, "order", "123")]
    [InlineData("order:*", true, "order", null)]
    [InlineData("invalid", false, null, null)]
    [InlineData("", false, null, null)]
    public void TryParse_ShouldReturnCorrectResult(string input, bool expectedSuccess, string? expectedConcept, string? expectedId)
    {
        var success = TagPattern.TryParse(input, out var result);

        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess)
        {
            Assert.Equal(expectedConcept, result.Concept);
            Assert.Equal(expectedId, result.Id);
        }
    }

    #endregion

    #region Factory Methods Tests

    [Fact]
    public void Exact_ShouldCreateExactPattern()
    {
        var pattern = TagPattern.Exact("order", "123");

        Assert.Equal("order", pattern.Concept);
        Assert.Equal("123", pattern.Id);
        Assert.True(pattern.IsExact);
        Assert.False(pattern.IsWildcard);
        Assert.Equal("order:123", pattern.Value);
    }

    [Fact]
    public void Exact_FromEventTag_ShouldCreateExactPattern()
    {
        var tag = new EventTag("customer", "456");
        var pattern = TagPattern.Exact(tag);

        Assert.Equal("customer", pattern.Concept);
        Assert.Equal("456", pattern.Id);
        Assert.True(pattern.IsExact);
    }

    [Fact]
    public void Prefix_ShouldCreateWildcardPattern()
    {
        var pattern = TagPattern.Prefix("order");

        Assert.Equal("order", pattern.Concept);
        Assert.Null(pattern.Id);
        Assert.True(pattern.IsWildcard);
        Assert.False(pattern.IsExact);
        Assert.Equal("order:*", pattern.Value);
        Assert.Equal("order:", pattern.ConceptPrefix);
    }

    #endregion

    #region Matches Tests

    [Theory]
    [InlineData("order:123", "order", "123", true)]
    [InlineData("order:123", "order", "456", false)]
    [InlineData("order:123", "customer", "123", false)]
    public void Matches_ExactPattern_WithEventTag_ShouldMatchCorrectly(string patternStr, string tagConcept, string tagId, bool expected)
    {
        var pattern = TagPattern.Parse(patternStr);
        var tag = new EventTag(tagConcept, tagId);

        Assert.Equal(expected, pattern.Matches(tag));
    }

    [Theory]
    [InlineData("order:*", "order", "123", true)]
    [InlineData("order:*", "order", "456", true)]
    [InlineData("order:*", "order", "abc-xyz", true)]
    [InlineData("order:*", "customer", "123", false)]
    public void Matches_WildcardPattern_WithEventTag_ShouldMatchCorrectly(string patternStr, string tagConcept, string tagId, bool expected)
    {
        var pattern = TagPattern.Parse(patternStr);
        var tag = new EventTag(tagConcept, tagId);

        Assert.Equal(expected, pattern.Matches(tag));
    }

    [Theory]
    [InlineData("order:123", "order:123", true)]
    [InlineData("order:123", "order:456", false)]
    [InlineData("order:*", "order:123", true)]
    [InlineData("order:*", "order:anything", true)]
    [InlineData("order:*", "customer:123", false)]
    public void Matches_WithTagValue_ShouldMatchCorrectly(string patternStr, string tagValue, bool expected)
    {
        var pattern = TagPattern.Parse(patternStr);

        Assert.Equal(expected, pattern.Matches(tagValue));
    }

    #endregion

    #region Equality Tests

    [Fact]
    public void Equals_SameExactPattern_ShouldBeEqual()
    {
        var pattern1 = TagPattern.Parse("order:123");
        var pattern2 = TagPattern.Parse("order:123");

        Assert.Equal(pattern1, pattern2);
        Assert.True(pattern1 == pattern2);
        Assert.False(pattern1 != pattern2);
        Assert.Equal(pattern1.GetHashCode(), pattern2.GetHashCode());
    }

    [Fact]
    public void Equals_SameWildcardPattern_ShouldBeEqual()
    {
        var pattern1 = TagPattern.Prefix("order");
        var pattern2 = TagPattern.Parse("order:*");

        Assert.Equal(pattern1, pattern2);
        Assert.True(pattern1 == pattern2);
    }

    [Fact]
    public void Equals_DifferentPatterns_ShouldNotBeEqual()
    {
        var exact = TagPattern.Parse("order:123");
        var wildcard = TagPattern.Parse("order:*");

        Assert.NotEqual(exact, wildcard);
        Assert.True(exact != wildcard);
    }

    #endregion

    #region Implicit Conversion Tests

    [Fact]
    public void ImplicitConversion_EventTagToTagPattern_ShouldCreateExact()
    {
        var tag = new EventTag("order", "123");
        TagPattern pattern = tag;

        Assert.True(pattern.IsExact);
        Assert.Equal("order", pattern.Concept);
        Assert.Equal("123", pattern.Id);
    }

    [Fact]
    public void ImplicitConversion_TagPatternToString_ShouldReturnValue()
    {
        var pattern = TagPattern.Parse("order:*");
        string value = pattern;

        Assert.Equal("order:*", value);
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void ToString_ExactPattern_ShouldReturnValue()
    {
        var pattern = TagPattern.Exact("order", "123");

        Assert.Equal("order:123", pattern.ToString());
    }

    [Fact]
    public void ToString_WildcardPattern_ShouldReturnValueWithStar()
    {
        var pattern = TagPattern.Prefix("order");

        Assert.Equal("order:*", pattern.ToString());
    }

    #endregion
}
