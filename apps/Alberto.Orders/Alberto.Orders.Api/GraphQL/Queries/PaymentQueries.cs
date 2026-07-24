using Alberto.Dcb;
using Alberto.Dcb.Postgres;
using Alberto.Orders.Api.GraphQL.Types;
using Alberto.Payments.Core.Payment;
using Alberto.Payments.Infrastructure;
using Alberto.Payments.Infrastructure.Projections;
using Alberto.Payments.Infrastructure.ReadModels;
using Npgsql;
using PaymentBoundary = Alberto.Payments.Core.Payment.PaymentDecider;

namespace Alberto.Orders.Api.GraphQL.Queries;

/// <summary>
/// GraphQL queries for payments.
/// </summary>
public static class PaymentQueries
{
    private static readonly PaymentEvolver _evolver = new();

    /// <summary>
    /// Gets a payment by ID from the event store (real-time, consistent).
    /// </summary>
    [Query]
    [GraphQLDescription("Gets a payment by ID, rebuilt from events for consistency.")]
    public static async Task<Payment?> GetPayment(
        Guid paymentId,
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
    [GraphQLDescription("Gets aggregated payment statistics from the async projection. Spans all tenants.")]
    public static async Task<PaymentsOverview?> GetPaymentsOverview(
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var stateStore = CreateStateStore<PaymentsOverview>(sp, nameof(PaymentsOverviewProjection));

        var states = await stateStore.LoadManyAsync(
            [PaymentsOverviewProjection.DocumentId],
            ct: ct);

        return states.GetValueOrDefault(PaymentsOverviewProjection.DocumentId);
    }

    /// <summary>
    /// Gets recent payments from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets recent payments from the projection, ordered by last update. Spans all tenants.")]
    public static async Task<IReadOnlyList<Payment>> GetRecentPayments(
        [Service] IServiceProvider sp,
        int limit = 20,
        CancellationToken ct = default)
    {
        // Cross-tenant, like every read off this store. IStateStore.ListRecentAsync has no
        // predicate to narrow by, which is precisely why the Orders example puts its per-tenant
        // list query on the EF projection (OrderQueries.GetOrders) instead of a JSONB one.
        var stateStore = CreateStateStore<PaymentSummary>(sp, nameof(PaymentSummaryProjection));

        var summaries = await stateStore.ListRecentAsync(limit, ct);
        return summaries.Select(Payment.FromSummary).ToList();
    }

    #region Helper Methods

    /// <summary>
    /// Builds a reader over a payments projection, constructed exactly like the writer in
    /// <c>PaymentsModule</c> — tenant-agnostic. The async projection consumes events for every
    /// tenant through one singleton store, so its rows live under the single-tenant primary key;
    /// a tenant-scoped store would query a different key and always come back empty.
    /// </summary>
    private static PostgresStateStore<TState> CreateStateStore<TState>(
        IServiceProvider sp,
        string projectionType)
    {
        var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(PaymentsModule.ModuleKey);
        return new PostgresStateStore<TState>(dataSource, projectionType, "payments");
    }

    private static async Task<PaymentState> LoadPaymentState(
        IEventStoreBackend backend,
        Guid paymentId,
        CancellationToken ct)
    {
        var events = await backend.StreamAsync(PaymentBoundary.BoundaryFor(paymentId), cancellationToken: ct);
        return _evolver.Reconstitute(events);
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
