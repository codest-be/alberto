using Alberto.Example;
using Alberto.Example.Modules.Orders;
using Alberto.Example.Modules.Payments;
using Alberto.EventStore;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddScoped<ITenantContext, TenantContext>();

builder.Services
    .AddEventStore()
    .AddEventStoreTelemetry()
    .Services
    .AddOrdersModule(builder.Configuration)
    .AddPaymentsModule(builder.Configuration);

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapOrdersModule();
app.MapPaymentsModule();

app.Run();

public partial class Program
{
}