using Alberto.Tenancy;
using Xunit;

namespace Alberto.Tests.Tenancy;

public class TenancyTests
{
    #region TenantContext Tests

    [Fact]
    public void TenantContext_InitialState_ShouldHaveNullTenantId()
    {
        var context = new TenantContext();

        Assert.Null(context.TenantId);
    }

    [Fact]
    public void TenantContext_SetTenant_ShouldStoreTenantId()
    {
        var context = new TenantContext();

        context.SetTenant("tenant_123");

        Assert.Equal("tenant_123", context.TenantId);
    }

    [Fact]
    public void TenantContext_SetTenant_ShouldOverwritePreviousTenant()
    {
        var context = new TenantContext();
        context.SetTenant("tenant1");

        context.SetTenant("tenant2");

        Assert.Equal("tenant2", context.TenantId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void TenantContext_SetTenant_WithInvalidValue_ShouldThrow(string? invalidTenantId)
    {
        var context = new TenantContext();

        Assert.ThrowsAny<ArgumentException>(() => context.SetTenant(invalidTenantId!));
    }

    /// <summary>
    /// The predicate and the setter answer the same question, so they must never disagree.
    /// A caller that gates on the predicate and then calls the setter — which is the whole
    /// reason the predicate exists — turns any disagreement in the permissive direction into an
    /// unhandled exception well past the point where it could be reported usefully. That is
    /// exactly what the Orders API's tenant interceptor did while it carried its own copy of
    /// the rule: <c>X-Tenant-Id: a-b-c</c> passed the copy, threw inside SetTenant, and reached
    /// the caller as "Unexpected Execution Error".
    /// </summary>
    [Theory]
    // Accepted.
    [InlineData("acme")]
    [InlineData("tenant_123")]
    [InlineData("a")]
    // Rejected — the shapes the interceptor's copy used to wave through.
    [InlineData("a-b-c")]
    [InlineData("Acme")]
    [InlineData("1acme")]
    [InlineData("acme.corp")]
    [InlineData("acme corp")]
    [InlineData("_acme")]
    // Rejected — absent or blank.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidTenantId_Agrees_With_SetTenant(string? tenantId)
    {
        var context = new TenantContext();

        var accepted = true;
        try { context.SetTenant(tenantId!); }
        catch (ArgumentException) { accepted = false; }

        Assert.Equal(accepted, TenantContext.IsValidTenantId(tenantId));
    }

    [Fact]
    public void IsValidTenantId_Agrees_With_SetTenant_AtTheLengthLimit()
    {
        // 63 characters is the PostgreSQL identifier limit the rule is pinned to; the
        // interceptor's copy allowed 64.
        Assert.True(TenantContext.IsValidTenantId("t" + new string('x', 62)));
        Assert.False(TenantContext.IsValidTenantId("t" + new string('x', 63)));
    }

    #endregion

    #region TenantAccessor Tests

    [Fact]
    public void TenantAccessor_TenantId_WhenSet_ShouldReturnValue()
    {
        var context = new TenantContext();
        context.SetTenant("tenant_123");
        var accessor = new TenantAccessor(context);

        Assert.Equal("tenant_123", accessor.TenantId);
    }

    [Fact]
    public void TenantAccessor_TenantId_WhenNotSet_ShouldThrow()
    {
        var context = new TenantContext();
        var accessor = new TenantAccessor(context);

        var ex = Assert.Throws<InvalidOperationException>(() => accessor.TenantId);
        Assert.Contains("No tenant context", ex.Message);
    }

    [Fact]
    public void TenantAccessor_TenantIdOrDefault_WhenSet_ShouldReturnValue()
    {
        var context = new TenantContext();
        context.SetTenant("tenant456");
        var accessor = new TenantAccessor(context);

        Assert.Equal("tenant456", accessor.TenantIdOrDefault);
    }

    [Fact]
    public void TenantAccessor_TenantIdOrDefault_WhenNotSet_ShouldReturnNull()
    {
        var context = new TenantContext();
        var accessor = new TenantAccessor(context);

        Assert.Null(accessor.TenantIdOrDefault);
    }

    [Fact]
    public void TenantAccessor_HasTenant_WhenSet_ShouldReturnTrue()
    {
        var context = new TenantContext();
        context.SetTenant("tenant789");
        var accessor = new TenantAccessor(context);

        Assert.True(accessor.HasTenant);
    }

    [Fact]
    public void TenantAccessor_HasTenant_WhenNotSet_ShouldReturnFalse()
    {
        var context = new TenantContext();
        var accessor = new TenantAccessor(context);

        Assert.False(accessor.HasTenant);
    }

    [Fact]
    public void TenantAccessor_ShouldReflectContextChanges()
    {
        var context = new TenantContext();
        var accessor = new TenantAccessor(context);

        Assert.False(accessor.HasTenant);

        context.SetTenant("newtenant");

        Assert.True(accessor.HasTenant);
        Assert.Equal("newtenant", accessor.TenantId);
    }

    #endregion
}
