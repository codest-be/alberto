using Alberto.Example;
using EventStore;
using EventStore.MultiTenant;
using EventStore.Postgres;
using EventStore.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddScoped<ITenantContext, TenantContext>();

builder.Services
    .AddEventStore()
    .AddPostgresEventStore("orders", o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("alberto-db") ??
                             throw new InvalidOperationException("Connection string 'alberto-db' not found.");
        o.Schema = "orders";
    })
    .AddPostgresEventStore("payments", o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("alberto-db") ??
                             throw new InvalidOperationException("Connection string 'alberto-db' not found.");
        o.Schema = "payments";
    })
    .AddTelemetry();


var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", () => "Hello World!");

app.Run();

public partial class Program
{
}