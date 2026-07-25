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
        var tenantId = GetTenantId(context);
        var stateStore = CreateStateStore<PaymentsOverview>(sp, tenantId, nameof(PaymentsOverviewProjection));

        var states = await stateStore.LoadManyAsync(
            ["overview"],
            ct: ct);

        return states.GetValueOrDefault("overview");
    }

    /// <summary>
    /// Gets recent payments from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets recent payments from the projection, ordered by last update.")]
    public static async Task<IReadOnlyList<Payment>> GetRecentPayments(
        IResolverContext context,
        [Service] IServiceProvider sp,
        int limit = 20,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var stateStore = CreateStateStore<PaymentSummary>(sp, tenantId, nameof(PaymentSummaryProjection));

        var summaries = await stateStore.ListRecentAsync(limit, ct);
        return summaries.Select(Payment.FromSummary).ToList();
    }

    #region Helper Methods

    private static string GetTenantId(IResolverContext context) =>
        context.GetGlobalState<string>(TenantHttpRequestInterceptor.TenantIdKey)
        ?? throw new InvalidOperationException("Tenant ID not found in resolver context");

    private static PostgresStateStore<TState> CreateStateStore<TState>(
        IServiceProvider sp,
        string tenantId,
        string projectionType)
    {
        var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(PaymentsModule.ModuleKey);

        // Named arguments, and LiveVersion rather than the default: a read side that pins its
        // rebuild version keeps serving the old copy after a rebuild is promoted.
        return new PostgresStateStore<TState>(
            dataSource,
            projectionType: projectionType,
            schema: "payments",
            rebuildVersion: ProjectionVersions.LiveVersion(sp, PaymentsModule.ModuleKey, projectionType),
            tenantId: tenantId);
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
