using EventStore;
using EventStore.MultiTenant;
using EventStore.Postgres;
using EventStore.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddScoped<ITenantContext, TenantContext>();

builder.Services
    .AddEventStore()
    .AddPostgresEventStore(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("eventstore-db")!;
        o.Schema = "app";
    })
    .AddTelemetry();


var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", () => "Hello World!");

app.Run();


public sealed class TenantContext : ITenantContext
{
    public Tenant Tenant => new Tenant("default");
}