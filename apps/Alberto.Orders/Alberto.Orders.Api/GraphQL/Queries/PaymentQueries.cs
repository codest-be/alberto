using System.Text.Json;
using Alberto.Dcb;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
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
        // PaymentsOverviewProjection blends every tenant into one document, stored under
        // TenantScope.CrossTenant. Reading it with the request's tenant would look correct
        // and return nothing.
        var stateStore = ReaderFor<PaymentsOverview>(
            sp, nameof(PaymentsOverviewProjection), TenantScope.CrossTenant);

        var states = await stateStore.LoadManyAsync(
            ["overview"],
            ct: ct);

        return states.GetValueOrDefault("overview");
    }

    /// <summary>
    /// Gets recent payments from the async projection.
    /// </summary>
    [Query]
    [GraphQLDescription("Gets the calling tenant's recent payments, ordered by last update.")]
    public static async Task<IReadOnlyList<Payment>> GetRecentPayments(
        IResolverContext context,
        [Service] IServiceProvider sp,
        int limit = 20,
        CancellationToken ct = default)
    {
        // Unlike the two *Overview projections, PaymentSummaryProjection is not an aggregate —
        // it keys one document per PaymentId, so its documents belong to individual tenants and
        // this field must be read under one. The store is scoped to the request's tenant, which
        // is where the filtering happens: a tenant-enabled module's projection rows are keyed by
        // tenant, so there is no unscoped read to accidentally fall back to.
        var stateStore = ReaderFor<PaymentSummary>(
            sp, nameof(PaymentSummaryProjection), GetTenantId(context));

        var summaries = await stateStore.ListRecentAsync(limit, ct);
        return summaries.Select(Payment.FromSummary).ToList();
    }

    #region Helper Methods

    /// <summary>
    /// Resolves the read-side store for one of the Payments module's projections, scoped to
    /// <paramref name="tenantId"/>.
    /// </summary>
    /// <remarks>
    /// The factory this resolves is the one <c>PaymentsModule</c> gave
    /// <see cref="Alberto.Dcb.DcbModuleBuilderExtensions.AddProjection{TState}"/>, so the reader
    /// cannot disagree with the writer about schema, projection type, rebuild version, or
    /// tenancy mode — the only thing decided here is which tenant to read. Constructing a store
    /// independently is what let this file read under a <c>(tenant_id, projection_type, …)</c>
    /// key while the writer stored rows under <c>(projection_type, …)</c>, and return nothing
    /// for as long as it did.
    /// </remarks>
    private static IStateStore<TState> ReaderFor<TState>(
        IServiceProvider sp,
        string projectionType,
        string? tenantId)
    {
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<TState>>>(
            $"{PaymentsModule.ModuleKey}:{projectionType}");

        return factory(tenantId);
    }

    private static string GetTenantId(IResolverContext context) =>
        context.GetGlobalState<string>(TenantHttpRequestInterceptor.TenantIdKey)
        ?? throw new InvalidOperationException("Tenant ID not found in resolver context");

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
