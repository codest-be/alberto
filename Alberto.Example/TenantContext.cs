using EventStore.MultiTenant;

namespace Alberto.Example;

public sealed class TenantContext : ITenantContext
{
    public Tenant Tenant => new Tenant("default");
}