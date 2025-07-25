namespace EventStore.MultiTenant;

public interface ITenantContext
{
    Tenant Tenant { get; }
}

public record struct Tenant(string Id);