namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Base interface for EF projection entities.
/// Provides the required columns for tenant isolation and document identification.
/// </summary>
public interface IProjectionEntity
{
    /// <summary>
    /// The tenant ID for multi-tenant isolation.
    /// Part of the composite primary key.
    /// </summary>
    string TenantId { get; set; }

    /// <summary>
    /// The document ID that uniquely identifies this entity within a tenant.
    /// Part of the composite primary key.
    /// </summary>
    string DocumentId { get; set; }

    /// <summary>
    /// Timestamp of the last update to this entity.
    /// Used for ordering and tracking changes.
    /// </summary>
    DateTimeOffset UpdatedAt { get; set; }
}
