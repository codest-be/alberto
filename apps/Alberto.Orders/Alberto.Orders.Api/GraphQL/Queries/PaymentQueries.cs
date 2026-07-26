using System.Text.Json;
using Alberto.Dcb;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Orders.Api.GraphQL.Types;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Core.Payment;
using Alberto.Payments.Infrastructure;
using Alberto.Payments.Infrastructure.Projections;
using Alberto.Payments.Infrastructure.ReadModels;
using HotChocolate.Resolvers;
using Npgsql;
using PaymentActions = Alberto.Payments.Core.Payment.Actions.PaymentDecider;
using PaymentBoundary = Alberto.Payments.Core.Payment.PaymentDecider;

namespace Alberto.Orders.Api.GraphQL.Queries;

/// <summary>
/// GraphQL queries for payments.
/// </summary>
public static class PaymentQueries
{
    /// <summary>
    /// Gets a payment by ID from the event store (real-time, consistent).
    /// </summary>
    [Query]
    [GraphQLDescription("Gets a payment by ID, rebuilt from events for consistency.")]
    public static async Task<Payment?> GetPayment(
        Guid paymentId,
        IResolverContext context,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(PaymentsModule.ModuleKey);
        var state = await LoadPaymentState(backend, paymentId, ct);

        if (!state.Exists)
            return null;

        return ToGraphQL(state);
    }

    /// <summary>
    /// Gets the payments overview statistics from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets aggregated payment statistics from the async projection.")]
    public static async Task<PaymentsOverview?> GetPaymentsOverview(
        IResolverContext context,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        // PaymentsOverviewProjection is a cross-tenant aggregate; resolve from DI so
        // the reader inherits the writer's no-tenant configuration (see CreateStateStore).
        var stateStore = CreateStateStore<PaymentsOverview>(sp, nameof(PaymentsOverviewProjection));

        var states = await stateStore.LoadManyAsync(
            ["overview"],
            ct: ct);

        return states.GetValueOrDefault("overview");
    }

    /// <summary>
    /// Gets recent payments from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription(
        "Gets recent payments from the projection, ordered by last update. "
        + "Not tenant-scoped: the Payments example stores no tenant, so this returns "
        + "payments from every tenant regardless of X-Tenant-Id.")]
    public static async Task<IReadOnlyList<Payment>> GetRecentPayments(
        IResolverContext context,
        [Service] IServiceProvider sp,
        int limit = 20,
        CancellationToken ct = default)
    {
        // Unlike the two *Overview projections, PaymentSummaryProjection is NOT an
        // aggregate — it keys one document per PaymentId. Reading it without a tenantId
        // therefore returns individual payment records belonging to every tenant, not a
        // blended total. That is what the Payments write side actually stores: PaymentsModule
        // declares no tenancy and PaymentSummary carries no tenant column, so there is
        // nothing to filter on. The per-request tenant this resolver used to pass was
        // decoration that only ever made the query return nothing. Tenant-scoping this
        // field needs a tenant on the payments projection first — see CLAUDE.md § Known Gaps.
        var stateStore = CreateStateStore<PaymentSummary>(sp, nameof(PaymentSummaryProjection));

        var summaries = await stateStore.ListRecentAsync(limit, ct);
        return summaries.Select(Payment.FromSummary).ToList();
    }

    #region Helper Methods

    /// <summary>
    /// Creates a read-side store for a cross-tenant aggregate projection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No <c>tenantId</c> is passed. <c>PaymentsOverviewProjection</c> and
    /// <c>PaymentSummaryProjection</c> are cross-tenant aggregates whose writers
    /// (in <c>PaymentsModule</c>) do not set a <c>tenantId</c> either. Passing a
    /// per-request <c>tenantId</c> here caused these readers to query under a
    /// <c>(tenant_id, projection_type, …)</c> primary key while the writer stored
    /// rows under <c>(projection_type, …)</c>, so they always returned nothing.
    /// </para>
    /// <para>
    /// <see cref="Alberto.Dcb.DcbModuleBuilderExtensions.AddProjection{TState}"/>
    /// also registers a <c>Func&lt;IStateStore&lt;TState&gt;&gt;</c> keyed by
    /// <c>"{moduleKey}:{processorId}"</c> that readers can resolve from DI to
    /// obtain the same store configuration automatically. This helper constructs
    /// the store directly because it needs <see cref="PostgresStateStore{TState}"/>
    /// (for <see cref="PostgresStateStore{TState}.ListRecentAsync"/>) rather than
    /// the narrower <see cref="Alberto.Dcb.Subscriptions.IStateStore{TState}"/>.
    /// </para>
    /// </remarks>
    private static PostgresStateStore<TState> CreateStateStore<TState>(
        IServiceProvider sp,
        string projectionType)
    {
        var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(PaymentsModule.ModuleKey);

        // rebuildVersion uses LiveVersion so the reader follows a promotion without being
        // rebuilt, exactly as the comment in the original code stated.
        return new PostgresStateStore<TState>(
            dataSource,
            projectionType: projectionType,
            schema: "payments",
            rebuildVersion: ProjectionVersions.LiveVersion(sp, PaymentsModule.ModuleKey, projectionType));
        // Note: tenantId is intentionally omitted — these are cross-tenant aggregates.
    }

    private static async Task<PaymentState> LoadPaymentState(
        IEventStoreBackend backend,
        Guid paymentId,
        CancellationToken ct)
    {
        var decider = new PaymentActions();
        var state = new PaymentState();

        var events = await backend.StreamAsync(PaymentBoundary.BoundaryFor(paymentId), cancellationToken: ct);

        foreach (var envelope in events)
        {
            var eventType = envelope.EventType.Id;
            object? domainEvent = eventType switch
            {
                "payment-initiated" => JsonSerializer.Deserialize<PaymentInitiated>(envelope.EventData),
                "payment-authorized" => JsonSerializer.Deserialize<PaymentAuthorized>(envelope.EventData),
                "payment-captured" => JsonSerializer.Deserialize<PaymentCaptured>(envelope.EventData),
                "payment-failed" => JsonSerializer.Deserialize<PaymentFailed>(envelope.EventData),
                "payment-refunded" => JsonSerializer.Deserialize<PaymentRefunded>(envelope.EventData),
                _ => null
            };

            if (domainEvent is null) continue;

            state = domainEvent switch
            {
                PaymentInitiated e => decider.Apply(state, e),
                PaymentAuthorized e => decider.Apply(state, e),
                PaymentCaptured e => decider.Apply(state, e),
                PaymentFailed e => decider.Apply(state, e),
                PaymentRefunded e => decider.Apply(state, e),
                _ => state
            };
        }

        return state;
    }

    private static Payment ToGraphQL(PaymentState state) => new(
        state.PaymentId,
        state.OrderId,
        state.Amount,
        state.Currency,
        state.PaymentMethod,
        state.Status,
        state.AuthorizationCode,
        state.ErrorCode,
        state.ErrorMessage,
        state.RefundedAmount,
        DateTimeOffset.MinValue, // Would need to track CreatedAt in state
        state.AuthorizedAt,
        state.CapturedAt,
        state.RefundedAt);

    #endregion
}
