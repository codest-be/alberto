using Alberto.Dcb.Tenancy;
using Xunit;

namespace Alberto.Dcb.Tests.Tenancy;

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
