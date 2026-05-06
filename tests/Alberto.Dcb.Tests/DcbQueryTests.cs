using Xunit;

namespace Alberto.Dcb.Tests;

public class DcbQueryTests
{
    #region Factory Method Tests

    [Fact]
    public void Empty_ShouldCreateQueryWithNoFilters()
    {
        var query = DcbQuery.Empty;

        Assert.True(query.IsEmpty);
        Assert.Empty(query.Types);
        Assert.Empty(query.TagPatterns);
        Assert.Empty(query.Tags);
        Assert.Empty(query.WildcardPatterns);
        Assert.False(query.HasWildcardPatterns);
    }

    [Fact]
    public void ByTypes_WithEventTypes_ShouldCreateTypesOnlyQuery()
    {
        var query = DcbQuery.ByTypes(new EventType("order-placed"), new EventType("order-cancelled"));

        Assert.Equal(2, query.Types.Count);
        Assert.Contains(query.Types, t => t.Id == "order-placed");
        Assert.Contains(query.Types, t => t.Id == "order-cancelled");
        Assert.True(query.HasTypesOnly);
        Assert.False(query.HasTagsOnly);
        Assert.False(query.HasTypesAndTags);
        Assert.False(query.IsEmpty);
    }

    [Fact]
    public void ByTypes_WithStrings_ShouldCreateTypesOnlyQuery()
    {
        var query = DcbQuery.ByTypes("order-placed", "order-cancelled");

        Assert.Equal(2, query.Types.Count);
        Assert.Contains(query.Types, t => t.Id == "order-placed");
        Assert.Contains(query.Types, t => t.Id == "order-cancelled");
    }

    [Fact]
    public void ByTags_WithEventTags_ShouldCreateTagsOnlyQuery()
    {
        var query = DcbQuery.ByTags(new EventTag("order", "123"), new EventTag("customer", "456"));

        Assert.Equal(2, query.TagPatterns.Count);
        Assert.Equal(2, query.Tags.Count);
        Assert.Contains(query.Tags, t => t.Value == "order:123");
        Assert.Contains(query.Tags, t => t.Value == "customer:456");
        Assert.True(query.HasTagsOnly);
        Assert.False(query.HasTypesOnly);
        Assert.False(query.HasWildcardPatterns);
    }

    [Fact]
    public void ByTags_WithStrings_ShouldCreateTagsOnlyQuery()
    {
        var query = DcbQuery.ByTags("order:123", "customer:456");

        Assert.Equal(2, query.Tags.Count);
        Assert.Contains(query.Tags, t => t.Value == "order:123");
    }

    [Fact]
    public void ByAllTags_WithStrings_ShouldCreateAllTagsQuery()
    {
        var query = DcbQuery.ByAllTags("reader:123", "source:456");

        Assert.Equal(2, query.Tags.Count);
        Assert.True(query.RequiresAllTags);
        Assert.True(query.HasTagsOnly);
        Assert.False(query.HasWildcardPatterns);
    }

    [Fact]
    public void ByAllTags_WithWildcardPattern_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => DcbQuery.ByAllTags("reader:123", "source:*"));
    }

    [Fact]
    public void ByTagPatterns_WithWildcards_ShouldCreateWildcardQuery()
    {
        var query = DcbQuery.ByTagPatterns(TagPattern.Prefix("order"), TagPattern.Prefix("customer"));

        Assert.Equal(2, query.TagPatterns.Count);
        Assert.Equal(2, query.WildcardPatterns.Count);
        Assert.Empty(query.Tags); // No exact tags
        Assert.True(query.HasWildcardPatterns);
        Assert.True(query.HasTagsOnly);
    }

    [Fact]
    public void ByTagPatterns_WithStrings_ShouldParsePatterns()
    {
        var query = DcbQuery.ByTagPatterns("order:*", "customer:123");

        Assert.Equal(2, query.TagPatterns.Count);
        Assert.Single(query.WildcardPatterns);
        Assert.Single(query.Tags);
        Assert.True(query.HasWildcardPatterns);
    }

    [Fact]
    public void For_WithStringId_ShouldCreateSingleTagQuery()
    {
        var query = DcbQuery.For("order", "123");

        Assert.Single(query.Tags);
        Assert.Equal("order:123", query.Tags.First().Value);
        Assert.True(query.HasTagsOnly);
    }

    [Fact]
    public void For_WithGuidId_ShouldCreateSingleTagQuery()
    {
        var id = Guid.NewGuid();
        var query = DcbQuery.For("order", id);

        Assert.Single(query.Tags);
        Assert.Equal($"order:{id}", query.Tags.First().Value);
    }

    [Fact]
    public void For_WithIntId_ShouldCreateSingleTagQuery()
    {
        var query = DcbQuery.For("product", 42);

        Assert.Single(query.Tags);
        Assert.Equal("product:42", query.Tags.First().Value);
    }

    [Fact]
    public void For_WithLongId_ShouldCreateSingleTagQuery()
    {
        var query = DcbQuery.For("product", 9999999999L);

        Assert.Single(query.Tags);
        Assert.Equal("product:9999999999", query.Tags.First().Value);
    }

    [Fact]
    public void For_Generic_ShouldCreateSingleTagQuery()
    {
        var id = Guid.NewGuid();
        var query = DcbQuery.For<Guid>("order", id);

        Assert.Single(query.Tags);
        Assert.Equal($"order:{id}", query.Tags.First().Value);
    }

    [Fact]
    public void For_Chaining_ShouldAddTypes()
    {
        var id = Guid.NewGuid();
        var query = DcbQuery.For("order", id).WithTypes("order-placed", "order-cancelled");

        Assert.Single(query.Tags);
        Assert.Equal(2, query.Types.Count);
        Assert.True(query.HasTypesAndTags);
    }

    #endregion

    #region Builder Method Tests

    [Fact]
    public void WithTypes_ShouldAddTypes()
    {
        var query = DcbQuery.Empty
            .WithTypes(new EventType("order-placed"))
            .WithTypes(new EventType("order-cancelled"));

        Assert.Equal(2, query.Types.Count);
    }

    [Fact]
    public void WithTypes_Strings_ShouldAddTypes()
    {
        var query = DcbQuery.Empty
            .WithTypes("order-placed", "order-cancelled");

        Assert.Equal(2, query.Types.Count);
    }

    [Fact]
    public void WithTags_ShouldAddExactTags()
    {
        var query = DcbQuery.Empty
            .WithTags(new EventTag("order", "123"))
            .WithTags(new EventTag("customer", "456"));

        Assert.Equal(2, query.Tags.Count);
        Assert.False(query.HasWildcardPatterns);
    }

    [Fact]
    public void WithTags_Strings_ShouldAddExactTags()
    {
        var query = DcbQuery.Empty
            .WithTags("order:123", "customer:456");

        Assert.Equal(2, query.Tags.Count);
    }

    [Fact]
    public void WithTag_ShouldAddSingleTag()
    {
        var query = DcbQuery.Empty
            .WithTag("order", "123");

        Assert.Single(query.Tags);
        Assert.Equal("order:123", query.Tags.First().Value);
    }

    [Fact]
    public void WithTagPatterns_ShouldAddPatterns()
    {
        var query = DcbQuery.Empty
            .WithTagPatterns(TagPattern.Prefix("order"))
            .WithTagPatterns(TagPattern.Exact("customer", "123"));

        Assert.Equal(2, query.TagPatterns.Count);
        Assert.Single(query.WildcardPatterns);
        Assert.Single(query.Tags);
    }

    [Fact]
    public void WithTagPatterns_Strings_ShouldParseAndAddPatterns()
    {
        var query = DcbQuery.Empty
            .WithTagPatterns("order:*", "customer:*");

        Assert.Equal(2, query.WildcardPatterns.Count);
        Assert.True(query.HasWildcardPatterns);
    }

    [Fact]
    public void WithTagPrefix_ShouldAddWildcardPattern()
    {
        var query = DcbQuery.Empty
            .WithTagPrefix("order");

        Assert.Single(query.WildcardPatterns);
        Assert.Equal("order", query.WildcardPatterns.First().Concept);
        Assert.True(query.HasWildcardPatterns);
    }

    [Fact]
    public void CombiningTypesAndTags_ShouldSetHasTypesAndTags()
    {
        var query = DcbQuery.Empty
            .WithTypes("order-placed")
            .WithTags("order:123");

        Assert.True(query.HasTypesAndTags);
        Assert.False(query.HasTypesOnly);
        Assert.False(query.HasTagsOnly);
        Assert.False(query.IsEmpty);
    }

    [Fact]
    public void CombiningExactAndWildcardTags_ShouldIncludeBoth()
    {
        var query = DcbQuery.Empty
            .WithTags("order:123")
            .WithTagPatterns("customer:*");

        Assert.Equal(2, query.TagPatterns.Count);
        Assert.Single(query.Tags);
        Assert.Single(query.WildcardPatterns);
        Assert.True(query.HasWildcardPatterns);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void Tags_ShouldReturnOnlyExactMatches()
    {
        var query = DcbQuery.Empty
            .WithTagPatterns("order:123", "customer:*", "product:456");

        var tags = query.Tags;

        Assert.Equal(2, tags.Count);
        Assert.Contains(tags, t => t.Value == "order:123");
        Assert.Contains(tags, t => t.Value == "product:456");
        Assert.DoesNotContain(tags, t => t.Concept == "customer");
    }

    [Fact]
    public void WildcardPatterns_ShouldReturnOnlyWildcards()
    {
        var query = DcbQuery.Empty
            .WithTagPatterns("order:123", "customer:*", "product:*");

        var wildcards = query.WildcardPatterns;

        Assert.Equal(2, wildcards.Count);
        Assert.Contains(wildcards, p => p.Concept == "customer");
        Assert.Contains(wildcards, p => p.Concept == "product");
        Assert.DoesNotContain(wildcards, p => p.Concept == "order");
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void ToString_EmptyQuery_ShouldReturnStar()
    {
        var query = DcbQuery.Empty;

        Assert.Equal("*", query.ToString());
    }

    [Fact]
    public void ToString_TypesOnly_ShouldShowTypes()
    {
        var query = DcbQuery.ByTypes("order-placed", "order-cancelled");

        var result = query.ToString();

        Assert.Contains("types=", result);
        Assert.Contains("order-placed", result);
        Assert.Contains("order-cancelled", result);
    }

    [Fact]
    public void ToString_TagsOnly_ShouldShowTags()
    {
        var query = DcbQuery.ByTags("order:123");

        var result = query.ToString();

        Assert.Contains("tags=", result);
        Assert.Contains("order:123", result);
    }

    [Fact]
    public void ToString_AllTagsOnly_ShouldShowAllTagsMarker()
    {
        var query = DcbQuery.ByAllTags("reader:123", "source:456");

        var result = query.ToString();

        Assert.Contains("tags(all)=", result);
        Assert.Contains("reader:123", result);
        Assert.Contains("source:456", result);
    }

    [Fact]
    public void ToString_WildcardTags_ShouldShowWildcard()
    {
        var query = DcbQuery.ByTagPatterns("order:*");

        var result = query.ToString();

        Assert.Contains("tags=", result);
        Assert.Contains("order:*", result);
    }

    [Fact]
    public void ToString_TypesAndTags_ShouldShowBothWithAndByDefault()
    {
        var query = DcbQuery.Empty
            .WithTypes("order-placed")
            .WithTags("customer:123");

        var result = query.ToString();

        Assert.Contains("types=", result);
        Assert.Contains("tags=", result);
        Assert.Contains("AND", result);
    }

    [Fact]
    public void ToString_TypesAndTags_AsUnion_ShouldShowOr()
    {
        var query = DcbQuery.Empty
            .WithTypes("order-placed")
            .WithTags("customer:123")
            .AsUnion();

        var result = query.ToString();

        Assert.Contains("types=", result);
        Assert.Contains("tags=", result);
        Assert.Contains("OR", result);
    }

    #endregion

    #region CompositionMode Tests

    [Fact]
    public void Default_CompositionMode_ShouldBeIntersect()
    {
        var query = DcbQuery.Empty
            .WithTypes("order-placed")
            .WithTags("customer:123");

        Assert.Equal(CompositionMode.Intersect, query.CompositionMode);
        Assert.True(query.IntersectsTypesAndTags);
        Assert.False(query.UnionsTypesAndTags);
    }

    [Fact]
    public void AsUnion_ShouldFlipToUnion()
    {
        var query = DcbQuery.Empty
            .WithTypes("order-placed")
            .WithTags("customer:123")
            .AsUnion();

        Assert.Equal(CompositionMode.Union, query.CompositionMode);
        Assert.False(query.IntersectsTypesAndTags);
        Assert.True(query.UnionsTypesAndTags);
    }

    [Fact]
    public void AsIntersect_ShouldFlipBackToIntersect()
    {
        var query = DcbQuery.Empty
            .WithTypes("order-placed")
            .WithTags("customer:123")
            .AsUnion()
            .AsIntersect();

        Assert.Equal(CompositionMode.Intersect, query.CompositionMode);
    }

    [Fact]
    public void IntersectsTypesAndTags_RequiresBothAxes()
    {
        Assert.False(DcbQuery.ByTypes("order-placed").IntersectsTypesAndTags);
        Assert.False(DcbQuery.ByTags("order:123").IntersectsTypesAndTags);
    }

    [Fact]
    public void AsUnion_ShouldPreserveTypesTagsAndMatchMode()
    {
        var query = DcbQuery.ByAllTags("reader:1", "source:2")
            .WithTypes("source-followed")
            .AsUnion();

        Assert.Equal(2, query.TagPatterns.Count);
        Assert.Single(query.Types);
        Assert.Equal(TagMatchMode.All, query.TagMatchMode);
        Assert.Equal(CompositionMode.Union, query.CompositionMode);
    }

    #endregion

    #region Immutability Tests

    [Fact]
    public void WithTypes_ShouldNotModifyOriginal()
    {
        var original = DcbQuery.ByTypes("order-placed");
        var modified = original.WithTypes("order-cancelled");

        Assert.Single(original.Types);
        Assert.Equal(2, modified.Types.Count);
    }

    [Fact]
    public void WithTags_ShouldNotModifyOriginal()
    {
        var original = DcbQuery.ByTags("order:123");
        var modified = original.WithTags("customer:456");

        Assert.Single(original.Tags);
        Assert.Equal(2, modified.Tags.Count);
    }

    [Fact]
    public void WithTagPatterns_ShouldNotModifyOriginal()
    {
        var original = DcbQuery.ByTagPatterns("order:*");
        var modified = original.WithTagPatterns("customer:*");

        Assert.Single(original.TagPatterns);
        Assert.Equal(2, modified.TagPatterns.Count);
    }

    #endregion
}
