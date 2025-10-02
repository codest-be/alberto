using Alberto.Example;
using Alberto.Example.Modules.Orders;
using Alberto.Example.Modules.Payments;
using EventStore;
using EventStore.MultiTenant;
using EventStore.Postgres;
using EventStore.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddScoped<ITenantContext, TenantContext>();

builder.Services
    .AddOrdersModule(builder.Configuration)
    .AddPaymentsModule(builder.Configuration);

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", () => "Hello World!");

app.Run();

public partial class Program
{
}