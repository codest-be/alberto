using Alberto.EventStore;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Telemetry;
using Alberto.Example;
using Alberto.Example.Modules.Orders;
using Alberto.Example.Modules.Payments;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddScoped<ITenantContext, MultiTenantContext>();

builder.Services
    .AddEventStore()
    .AddMultiTenancy<MultiTenantContext>()
    .AddEventStoreTelemetry()
    .Services
    .AddOrdersModule(builder.Configuration)
    .AddPaymentsModule(builder.Configuration);

WebApplication app = builder.Build();

app.MapDefaultEndpoints();

app.MapOrdersModule();
app.MapPaymentsModule();

app.Run();

public partial class Program
{
}