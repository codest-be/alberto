using System.Text.Json;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.EventStore.Subscriptions;
using Alberto.Example;

namespace Alberto.Example.Modules.Payments;

public static class PaymentModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEventStore<PaymentEventStore, MultiTenantContext>("payments", options =>
        {
            options.ConnectionString = configuration.GetConnectionString("alberto-db") ??
                                       throw new InvalidOperationException("Connection string 'alberto-db' not found.");
            options.Schema = "payments";
        });

        return services;
    }

    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder payments = endpoints.MapGroup("payments");


        payments.MapPost("/",
            async Task<IResult> (CreatePaymentRequest request, PaymentEventStore eventStore, CancellationToken ctx) =>
            {
                Guid paymentId = Guid.NewGuid();
                PaymentCreated paymentCreated = new(paymentId, request.OrderId, request.Amount, DateTime.UtcNow);

                await eventStore.Append(
                    [
                        new EventToPersist
                        {
                            EventType = new EventType("PaymentCreated"),
                            EventJson = JsonSerializer.Serialize(paymentCreated),
                            Tags = [new EventTag("payment", paymentId.ToString())],
                            Metadata = new Dictionary<string, string>(),
                            Created = DateTimeOffset.UtcNow
                        }
                    ], new StreamQuery(
                        [new EventTag("payment", paymentId.ToString())],
                        [new EventType("PaymentCreated")]),
                    null,
                    ctx);

                return Results.Created($"/payments/{paymentId}", new { PaymentId = paymentId });
            });

        payments.MapGet("/{id:guid}",
            async Task<IResult> (Guid id, PaymentEventStore eventStore) =>
            {
                IReadOnlyCollection<IEventEnvelope> events =
                    await eventStore.Stream(new StreamQuery([new EventTag("payment", id.ToString())]));

                if (!events.Any())
                    return Results.NotFound();

                Payment payment = Payment.Create(events.ToArray());
                return Results.Ok(payment);
            });

        payments.MapPost("/{id:guid}/process",
            async Task<IResult> (Guid id, PaymentEventStore eventStore, CancellationToken ctx) =>
            {
                PaymentProcessed paymentProcessed = new(id, DateTime.UtcNow);

                await eventStore.Append(
                    [
                        new EventToPersist
                        {
                            EventType = new EventType("PaymentProcessed"),
                            EventJson = JsonSerializer.Serialize(paymentProcessed),
                            Tags = [new EventTag("payment", id.ToString())],
                            Metadata = new Dictionary<string, string>(),
                            Created = DateTimeOffset.UtcNow
                        }
                    ],
                    new StreamQuery([new EventTag("payment", id.ToString())]), null, ctx);

                return Results.Ok();
            });

        return endpoints;
    }
}

public record CreatePaymentRequest(Guid OrderId, decimal Amount);

[EventType("payment-created")]
public record PaymentCreated(Guid PaymentId, Guid OrderId, decimal Amount, DateTime CreatedAt);

[EventType("payment-processed")]
public record PaymentProcessed(Guid PaymentId, DateTime ProcessedAt);

public class Payment
{
    public static Payment Create(IEventEnvelope[] events)
    {
        return new Payment();
    }
}