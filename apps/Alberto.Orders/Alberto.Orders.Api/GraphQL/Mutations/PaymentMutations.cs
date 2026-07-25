using System.Text.Json;
using Alberto.Dcb;
using Alberto.Orders.Api.GraphQL.Types;
using Alberto.Payments.Core;
using Alberto.Payments.Core.Payment;
using Alberto.Payments.Infrastructure;
using HotChocolate.Resolvers;
using PaymentActions = Alberto.Payments.Core.Payment.Actions.PaymentDecider;
using PaymentBoundary = Alberto.Payments.Core.Payment.PaymentDecider;

namespace Alberto.Orders.Api.GraphQL.Mutations;

/// <summary>
/// GraphQL mutations for payment operations.
/// </summary>
public static class PaymentMutations
{
    private static readonly PaymentEvolver _evolver = new();

    /// <summary>
    /// Initiates a new payment for an order.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Initiates a new payment for an order.")]
    public static async Task<InitiatePaymentResult> InitiatePayment(
        InitiatePaymentInput input,
        IResolverContext context,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(PaymentsModule.ModuleKey);
        var paymentId = Guid.CreateVersion7();

        var state = new PaymentState();
        var result = PaymentActions.Initiate(state, paymentId, input.OrderId, input.Amount, input.Currency, input.PaymentMethod);

        await AppendEvents(eventStore, paymentId, input.OrderId, result, ct);

        return new InitiatePaymentResult(paymentId);
    }

    /// <summary>
    /// Authorizes a payment.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Authorizes a payment with an authorization code.")]
    public static async Task<MutationResult> AuthorizePayment(
        Guid paymentId,
        string authorizationCode,
        IResolverContext context,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(PaymentsModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(PaymentsModule.ModuleKey);

        var state = await LoadPaymentState(backend, paymentId, ct);
        var result = PaymentActions.Authorize(state, authorizationCode, timeProvider.GetUtcNow());

        await AppendEvents(eventStore, paymentId, state.OrderId, result, ct);

        return new MutationResult();
    }

    /// <summary>
    /// Captures a payment.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Captures a previously authorized payment.")]
    public static async Task<MutationResult> CapturePayment(
        CapturePaymentInput input,
        IResolverContext context,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(PaymentsModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(PaymentsModule.ModuleKey);

        var state = await LoadPaymentState(backend, input.PaymentId, ct);
        var result = PaymentActions.Capture(state, input.Amount, timeProvider.GetUtcNow());

        await AppendEvents(eventStore, input.PaymentId, state.OrderId, result, ct);

        return new MutationResult();
    }

    /// <summary>
    /// Marks a payment as failed.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Marks a payment as failed.")]
    public static async Task<MutationResult> FailPayment(
        FailPaymentInput input,
        IResolverContext context,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(PaymentsModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(PaymentsModule.ModuleKey);

        var state = await LoadPaymentState(backend, input.PaymentId, ct);
        var result = PaymentActions.Fail(state, input.ErrorCode, input.ErrorMessage);

        await AppendEvents(eventStore, input.PaymentId, state.OrderId, result, ct);

        return new MutationResult();
    }

    /// <summary>
    /// Refunds a captured payment.
    /// </summary>
    [Mutation]
    [GraphQLDescription("Refunds a previously captured payment.")]
    public static async Task<MutationResult> RefundPayment(
        RefundPaymentInput input,
        IResolverContext context,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var eventStore = sp.GetRequiredKeyedService<IEventStore>(PaymentsModule.ModuleKey);
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(PaymentsModule.ModuleKey);

        var state = await LoadPaymentState(backend, input.PaymentId, ct);
        var result = PaymentActions.Refund(state, input.Amount, input.Reason, timeProvider.GetUtcNow());

        await AppendEvents(eventStore, input.PaymentId, state.OrderId, result, ct);

        return new MutationResult();
    }

    #region Helper Methods

    private static async Task<PaymentState> LoadPaymentState(
        IEventStoreBackend backend,
        Guid paymentId,
        CancellationToken ct)
    {
        var events = await backend.StreamAsync(PaymentBoundary.BoundaryFor(paymentId), cancellationToken: ct);
        return _evolver.Reconstitute(events);
    }

    private static async Task AppendEvents(
        IEventStore eventStore,
        Guid paymentId,
        Guid orderId,
        Decision decision,
        CancellationToken ct)
    {
        if (decision.IsError)
            throw new InvalidOperationException(
                string.Join("; ", decision.Problems.Select(p => p.Message)));

        var toPersist = decision.Events.Select(@event => new EventToPersist
        {
            EventType = EventType.FromType(@event.GetType()),
            Tags = [
                new EventTag(Tags.Payment, paymentId.ToString()),
                new EventTag(Tags.Order, orderId.ToString())
            ],
            EventData = JsonSerializer.Serialize(@event, @event.GetType())
        }).ToArray();

        await eventStore.AppendAsync(toPersist, PaymentBoundary.BoundaryFor(paymentId), cancellationToken: ct);
    }

    #endregion
}
