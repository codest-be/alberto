using Alberto.Dcb.Admin;
using Alberto.Orders.Api.GraphQL;
using Alberto.Orders.Infrastructure;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Add OpenTelemetry
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("orders-api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

// Add services
builder.Services.AddSingleton(TimeProvider.System);

// Add Orders module
builder.Services.AddOrdersModule(builder.Configuration);

// Add GraphQL
builder.Services
    .AddGraphQLServer()
    .AddTypes();

var app = builder.Build();

// Configure pipeline
app.UseRouting();
app.MapGraphQL();
app.MapDcbAdmin();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

// Make Program class accessible for testing
public partial class Program;
