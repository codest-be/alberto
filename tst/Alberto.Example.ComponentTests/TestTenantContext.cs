using Alberto.EventStore.MultiTenant;

namespace Alberto.Example.ComponentTests;

internal sealed class TestTenantContext : ITenantContext
{
    // Use "default" to match the API's MultiTenantContext default tenant
    public Tenant Tenant { get; set; } = new("default");
}