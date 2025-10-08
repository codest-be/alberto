using Alberto.EventStore.MultiTenant;

namespace Alberto.Example.ComponentTests;

internal sealed class TestTenantContext : ITenantContext
{
    public Tenant Tenant { get; set; } = new("test-tenant");
}