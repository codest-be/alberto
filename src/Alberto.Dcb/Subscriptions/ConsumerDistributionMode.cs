namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Specifies how event processing is distributed across multiple instances.
/// </summary>
public enum ConsumerDistributionMode
{
    /// <summary>
    /// Single leader mode: One instance processes all tenants.
    /// Other instances wait as standby and take over if the leader fails.
    /// </summary>
    SingleLeader,

    /// <summary>
    /// Tenant-distributed mode: Multiple instances each claim different tenants.
    /// Events are processed by the instance that holds the lock for each tenant.
    /// </summary>
    TenantDistributed
}
