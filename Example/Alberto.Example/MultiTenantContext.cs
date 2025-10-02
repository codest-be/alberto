using Alberto.EventStore.MultiTenant;

namespace Alberto.Example;

public sealed class MultiTenantContext : ITenantContext
{
    private static readonly AsyncLocal<Tenant?> CurrentTenant = new();

    public Tenant Tenant
    {
        get => CurrentTenant.Value ??= new Tenant("default");
        set => CurrentTenant.Value = value;
    }
}