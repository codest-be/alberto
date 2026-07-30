using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Alberto.Dcb;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using Alberto.Payments.Platform;
using Npgsql;

namespace Alberto.Payments.Features;

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
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        // PaymentsOverviewProjection blends every tenant into one document, stored under
        // TenantScope.CrossTenant. Reading it with the request's tenant would look correct
        // and return nothing. The factory resolved here is the writer's own, so the only thing
        // this resolver decides is which tenant to read.
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<PaymentsOverview>>>(
            $"{PaymentsModule.ModuleKey}:{nameof(PaymentsOverviewProjection)}");

        var states = await factory(TenantScope.CrossTenant).LoadManyAsync(
            ["overview"],
            ct: ct);

        return states.GetValueOrDefault("overview");
    }

    #region Helper Methods

    private static async Task<PaymentState> LoadPaymentState(
        IEventStoreBackend backend,
        Guid paymentId,
        CancellationToken ct)
    {
        var decider = new PaymentDecider();
        var state = new PaymentState();

        var events = await backend.StreamAsync(PaymentDecider.BoundaryFor(paymentId), cancellationToken: ct);

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
