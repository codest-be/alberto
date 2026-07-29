using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Resolvers;
using HotChocolate.Subscriptions;
using HotChocolate.Types;

namespace Alberto.Dcb.Admin.GraphQL;

/// <summary>
/// GraphQL subscriptions for real-time admin updates.
/// Subscribers receive updates when admin operations are performed.
/// </summary>
/// <remarks>
/// <para>
/// Every stream is scoped to one module. The topic a subscriber listens on is derived from
/// the resolved module key, and mutations publish to the topic of the module they acted on,
/// so an operator watching Payments never sees an Orders mutation land on their screen.
/// </para>
/// <para>
/// Each pair below is a resolver plus an explicit <c>[Subscribe(With = …)]</c> method, rather
/// than a constant <c>[Topic]</c>, because the topic is only known once the module argument
/// is read. HotChocolate infers a field's arguments from the <b>resolver</b> signature, not
/// the subscribe method, which is why <c>module</c> is declared on the resolver — where it is
/// unused — and read back out of <see cref="IResolverContext"/> when subscribing.
/// </para>
/// </remarks>
public static class AdminSubscriptions
{
    private const string ModuleArgument = "module";

    /// <summary>
    /// Receives system info snapshots whenever an admin mutation is performed.
    /// The mutation publishes a fresh snapshot after completing.
    /// </summary>
    [Subscription]
    [GraphQLDescription("Streams system info snapshots after admin mutations on the given module.")]
    [Subscribe(With = nameof(SubscribeToSystemUpdated))]
    public static SystemInfo OnAdminSystemUpdated(
        [EventMessage] SystemInfo systemInfo,
        string? module) =>
        systemInfo;

    public static ValueTask<ISourceStream<SystemInfo>> SubscribeToSystemUpdated(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventReceiver receiver,
        IResolverContext context,
        CancellationToken ct) =>
        receiver.SubscribeAsync<SystemInfo>(TopicFor(AdminTopics.SystemUpdated, modules, context), ct);

    /// <summary>
    /// Receives checkpoint updates after set/reset mutations.
    /// </summary>
    [Subscription]
    [GraphQLDescription("Streams checkpoint updates after admin set/reset operations on the given module.")]
    [Subscribe(With = nameof(SubscribeToCheckpointUpdated))]
    public static CheckpointMutationResult OnAdminCheckpointUpdated(
        [EventMessage] CheckpointMutationResult result,
        string? module) =>
        result;

    public static ValueTask<ISourceStream<CheckpointMutationResult>> SubscribeToCheckpointUpdated(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventReceiver receiver,
        IResolverContext context,
        CancellationToken ct) =>
        receiver.SubscribeAsync<CheckpointMutationResult>(
            TopicFor(AdminTopics.CheckpointUpdated, modules, context), ct);

    /// <summary>
    /// Receives dead letter count changes after clear/retry mutations.
    /// </summary>
    [Subscription]
    [GraphQLDescription("Streams dead letter changes after clear/retry operations on the given module.")]
    [Subscribe(With = nameof(SubscribeToDeadLettersChanged))]
    public static DeadLetterClearResult OnAdminDeadLettersChanged(
        [EventMessage] DeadLetterClearResult result,
        string? module) =>
        result;

    public static ValueTask<ISourceStream<DeadLetterClearResult>> SubscribeToDeadLettersChanged(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventReceiver receiver,
        IResolverContext context,
        CancellationToken ct) =>
        receiver.SubscribeAsync<DeadLetterClearResult>(
            TopicFor(AdminTopics.DeadLettersChanged, modules, context), ct);

    /// <summary>
    /// Receives rebuild lifecycle events (started, promoted, aborted).
    /// </summary>
    [Subscription]
    [GraphQLDescription("Streams rebuild lifecycle events (started, promoted, aborted) on the given module.")]
    [Subscribe(With = nameof(SubscribeToRebuildUpdated))]
    public static AdminRebuildEvent OnAdminRebuildUpdated(
        [EventMessage] AdminRebuildEvent rebuildEvent,
        string? module) =>
        rebuildEvent;

    public static ValueTask<ISourceStream<AdminRebuildEvent>> SubscribeToRebuildUpdated(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventReceiver receiver,
        IResolverContext context,
        CancellationToken ct) =>
        receiver.SubscribeAsync<AdminRebuildEvent>(
            TopicFor(AdminTopics.RebuildUpdated, modules, context), ct);

    /// <summary>
    /// Receives audit trail entries for all admin operations.
    /// </summary>
    [Subscription]
    [GraphQLDescription("Streams every admin audit event on the given module as it is appended to the event log.")]
    [Subscribe(With = nameof(SubscribeToAuditEvent))]
    public static AdminAuditEntry OnAdminAuditEvent(
        [EventMessage] AdminAuditEntry entry,
        string? module) =>
        entry;

    public static ValueTask<ISourceStream<AdminAuditEntry>> SubscribeToAuditEvent(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventReceiver receiver,
        IResolverContext context,
        CancellationToken ct) =>
        receiver.SubscribeAsync<AdminAuditEntry>(
            TopicFor(AdminTopics.AuditEvent, modules, context), ct);

    private static string TopicFor(string topic, IAdminModuleRegistry modules, IResolverContext context) =>
        AdminTopics.ForModule(topic, modules.ResolveKey(context.ArgumentValue<string?>(ModuleArgument)));
}

/// <summary>Topic name constants for admin subscriptions.</summary>
public static class AdminTopics
{
    public const string SystemUpdated = "AdminSystemUpdated";
    public const string CheckpointUpdated = "AdminCheckpointUpdated";
    public const string DeadLettersChanged = "AdminDeadLettersChanged";
    public const string RebuildUpdated = "AdminRebuildUpdated";
    public const string AuditEvent = "AdminAuditEvent";

    /// <summary>
    /// Scopes a topic to a single module.
    /// </summary>
    /// <remarks>
    /// The separator is a double underscore rather than a colon or a dot so the result
    /// stays a plain identifier: the Postgres subscription backplane carries topic names
    /// through NOTIFY payloads, and there is nothing to gain from testing which
    /// punctuation survives that trip.
    /// </remarks>
    public static string ForModule(string topic, string moduleKey) => $"{topic}__{moduleKey}";
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
