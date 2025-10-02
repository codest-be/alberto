using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.EventStore.Postgres;

namespace Alberto.Example.Modules.Payments;

public static class PaymentModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
        => services
            .AddPostgresEventStore<PaymentEventStore>(o =>
            {
                o.ConnectionString = configuration.GetConnectionString("alberto-db") ??
                                     throw new InvalidOperationException("Connection string 'alberto-db' not found.");
                o.Schema = "payments";
            });

    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints)
    {
        var payments = endpoints.MapGroup("payments");


        payments.MapPost("/",
            async Task<IResult> (CreatePaymentRequest request, PaymentEventStore eventStore, CancellationToken ctx) =>
            {
                var paymentId = Guid.NewGuid();
                var paymentCreated =
                    new PaymentCreated(paymentId, request.OrderId, request.Amount, DateTime.UtcNow);

                await eventStore.Append(
                    [
                        new EventToPersist
                        {
                            EventType = new EventType("PaymentCreated"),
                            EventJson = System.Text.Json.JsonSerializer.Serialize(paymentCreated),
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
                var events = await eventStore.Stream(new StreamQuery(tags:
                    [new EventTag("payment", id.ToString())]));

                if (!events.Any())
                    return Results.NotFound();

                var payment = Payment.Create(events.ToArray());
                return Results.Ok(payment);
            });

        payments.MapPost("/{id:guid}/process",
            async Task<IResult> (Guid id, PaymentEventStore eventStore, CancellationToken ctx) =>
            {
                var paymentProcessed = new PaymentProcessed(id, DateTime.UtcNow);

                await eventStore.Append(
                    [
                        new EventToPersist
                        {
                            EventType = new EventType("PaymentProcessed"),
                            EventJson = System.Text.Json.JsonSerializer.Serialize(paymentProcessed),
                            Tags = [new EventTag("payment", id.ToString())],
                            Metadata = new Dictionary<string, string>(),
                            Created = DateTimeOffset.UtcNow
                        }
                    ],
                    new StreamQuery(tags: [new EventTag("payment", id.ToString())]), null, ctx);

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