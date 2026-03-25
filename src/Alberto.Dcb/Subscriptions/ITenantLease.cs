namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Represents a lease for processing a specific tenant.
/// The lease must be periodically renewed to maintain ownership.
/// </summary>
public interface ITenantLease
{
    /// <summary>
    /// The tenant ID this lease is for.
    /// </summary>
    string TenantId { get; }

    /// <summary>
    /// When this lease expires if not renewed.
    /// </summary>
    DateTimeOffset ExpiresAt { get; }
}

/// <summary>
/// Default implementation of ITenantLease.
/// </summary>
public sealed record TenantLease(string TenantId, DateTimeOffset ExpiresAt) : ITenantLease;
