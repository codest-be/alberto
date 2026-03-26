using System.Text.Json;
using Alberto.Dcb;
using Alberto.Orders.Api.GraphQL.Types;
using Alberto.Payments.Core;
using Alberto.Payments.Core.Events;
using Alberto.Payments.Core.Payment;
using Alberto.Payments.Core.Payment.Actions;
using Alberto.Payments.Infrastructure;
using HotChocolate;
using HotChocolate.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using PaymentActions = Alberto.Payments.Core.Payment.Actions.PaymentDecider;
using PaymentBoundary = Alberto.Payments.Core.Payment.PaymentDecider;

namespace Alberto.Orders.Api.GraphQL.Mutations;

/// <summary>
/// GraphQL mutations for payment operations.
/// </summary>
public static class PaymentMutations
{
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
        var decision = PaymentActions.Initiate(state, paymentId, input.OrderId, input.Amount, input.Currency, input.PaymentMethod);

        decision.EnsureSuccess();

        var events = new[]
        {
            new EventToPersist
            {
                EventType = EventType.FromType(decision.Event!.GetType()),
                Tags = [
                    new EventTag(Tags.Payment, paymentId.ToString()),
                    new EventTag(Tags.Order, input.OrderId.ToString())
                ],
                EventData = JsonSerializer.Serialize(decision.Event, decision.Event.GetType())
            }
        };

        await eventStore.AppendAsync(events, PaymentBoundary.BoundaryFor(paymentId), cancellationToken: ct);

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
        var decision = PaymentActions.Authorize(state, authorizationCode, timeProvider.GetUtcNow());

        decision.EnsureSuccess();

        await AppendEvent(eventStore, paymentId, state.OrderId, decision.Event!, ct);

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
        var decision = PaymentActions.Capture(state, input.Amount, timeProvider.GetUtcNow());

        decision.EnsureSuccess();

        await AppendEvent(eventStore, input.PaymentId, state.OrderId, decision.Event!, ct);

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
        var decision = PaymentActions.Fail(state, input.ErrorCode, input.ErrorMessage);

        decision.EnsureSuccess();

        await AppendEvent(eventStore, input.PaymentId, state.OrderId, decision.Event!, ct);

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
        var decision = PaymentActions.Refund(state, input.Amount, input.Reason, timeProvider.GetUtcNow());

        decision.EnsureSuccess();

        await AppendEvent(eventStore, input.PaymentId, state.OrderId, decision.Event!, ct);

        return new MutationResult();
    }

    #region Helper Methods

    private static async Task<PaymentState> LoadPaymentState(
        IEventStoreBackend backend,
        Guid paymentId,
        CancellationToken ct)
    {
        var decider = new PaymentActions();
        var state = new PaymentState();

        var events = await backend.Stream(PaymentBoundary.BoundaryFor(paymentId), cancellationToken: ct);

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

    private static async Task AppendEvent(
        IEventStore eventStore,
        Guid paymentId,
        Guid orderId,
        IEvent @event,
        CancellationToken ct)
    {
        var events = new[]
        {
            new EventToPersist
            {
                EventType = EventType.FromType(@event.GetType()),
                Tags = [
                    new EventTag(Tags.Payment, paymentId.ToString()),
                    new EventTag(Tags.Order, orderId.ToString())
                ],
                EventData = JsonSerializer.Serialize(@event, @event.GetType())
            }
        };

        await eventStore.AppendAsync(events, PaymentBoundary.BoundaryFor(paymentId), cancellationToken: ct);
    }

    #endregion
}
