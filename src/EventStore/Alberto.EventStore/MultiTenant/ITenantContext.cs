namespace Alberto.EventStore.MultiTenant;

public interface ITenantContext
{
    Tenant Tenant { get; }
}

public record struct Tenant(string Id);

public sealed class SingleTenantContext : ITenantContext
{
    public Tenant Tenant { get; } = new("default");
}