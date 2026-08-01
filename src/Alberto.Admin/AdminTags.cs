namespace Alberto.Admin;

/// <summary>
/// Tag keys used to tag admin audit events in the DCB event log.
/// Query with <c>alberto events --tag admin-rebuild</c> or via
/// <see cref="IAdminReader.GetEventsAsync"/> with the tag parameter.
/// </summary>
public static class AdminTags
{
    /// <summary>Checkpoint operations (reset, rewind).</summary>
    public const string Checkpoint = "admin-checkpoint";

    /// <summary>Dead letter operations (retry, dismiss, clear).</summary>
    public const string DeadLetter = "admin-dl";

    /// <summary>Projection rebuild operations (start, promote, abort).</summary>
    public const string Rebuild = "admin-rebuild";

    /// <summary>Outbox operations (purge).</summary>
    public const string Outbox = "admin-outbox";

    /// <summary>Tenant lease operations (release).</summary>
    public const string TenantLease = "admin-tenant-lease";
}
