using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Subscriptions;
using HotChocolate.Types;

namespace Alberto.Dcb.Admin.GraphQL;

/// <summary>
/// GraphQL subscriptions for real-time admin updates.
/// Subscribers receive updates when admin operations are performed.
/// </summary>
public static class AdminSubscriptions
{
    /// <summary>
    /// Receives system info snapshots whenever an admin mutation is performed.
    /// The mutation publishes a fresh snapshot after completing.
    /// </summary>
    [Subscription]
    [GraphQLDescription("Streams system info snapshots after admin mutations.")]
    [Topic(AdminTopics.SystemUpdated)]
    public static SystemInfo OnAdminSystemUpdated(
        [EventMessage] SystemInfo systemInfo) =>
        systemInfo;

    /// <summary>
    /// Receives checkpoint updates after set/reset mutations.
    /// </summary>
    [Subscription]
    [GraphQLDescription("Streams checkpoint updates after admin set/reset operations.")]
    [Topic(AdminTopics.CheckpointUpdated)]
    public static CheckpointMutationResult OnAdminCheckpointUpdated(
        [EventMessage] CheckpointMutationResult result) =>
        result;

    /// <summary>
    /// Receives dead letter count changes after clear/retry mutations.
    /// </summary>
    [Subscription]
    [GraphQLDescription("Streams dead letter changes after clear/retry operations.")]
    [Topic(AdminTopics.DeadLettersChanged)]
    public static DeadLetterClearResult OnAdminDeadLettersChanged(
        [EventMessage] DeadLetterClearResult result) =>
        result;

    /// <summary>
    /// Receives rebuild lifecycle events (started, promoted, aborted).
    /// </summary>
    [Subscription]
    [GraphQLDescription("Streams rebuild lifecycle events (started, promoted, aborted).")]
    [Topic(AdminTopics.RebuildUpdated)]
    public static AdminRebuildEvent OnAdminRebuildUpdated(
        [EventMessage] AdminRebuildEvent rebuildEvent) =>
        rebuildEvent;

    /// <summary>
    /// Receives audit trail entries for all admin operations.
    /// </summary>
    [Subscription]
    [GraphQLDescription("Streams every admin audit event as it is appended to the event log.")]
    [Topic(AdminTopics.AuditEvent)]
    public static AdminAuditEntry OnAdminAuditEvent(
        [EventMessage] AdminAuditEntry entry) =>
        entry;
}

/// <summary>Topic name constants for admin subscriptions.</summary>
public static class AdminTopics
{
    public const string SystemUpdated = "AdminSystemUpdated";
    public const string CheckpointUpdated = "AdminCheckpointUpdated";
    public const string DeadLettersChanged = "AdminDeadLettersChanged";
    public const string RebuildUpdated = "AdminRebuildUpdated";
    public const string AuditEvent = "AdminAuditEvent";
}

/// <summary>Envelope for rebuild lifecycle subscription messages.</summary>
public sealed record AdminRebuildEvent(
    string ProcessorId,
    string Action,
    string Status);

/// <summary>Envelope for admin audit trail subscription messages.</summary>
public sealed record AdminAuditEntry(
    string EventType,
    string OperatorId,
    string Description,
    DateTimeOffset Timestamp);
