using Alberto.Dcb.Admin;
using Alberto.Dcb.Admin.GraphQL;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Tenancy;
using Alberto.Orders.Api.GraphQL;
using Alberto.Orders.Infrastructure;
using Alberto.Payments.Infrastructure;
using HotChocolate.Diagnostics;
using Npgsql;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Add Aspire service defaults (telemetry, health checks, etc.)
builder.AddServiceDefaults();

// Add services
builder.Services.AddSingleton(TimeProvider.System);

// Add tenancy support
builder.Services.AddTenancy();

// Add Orders module
builder.Services.AddOrdersModule(builder.Configuration);

// Add Payments module
builder.Services.AddPaymentsModule(builder.Configuration);

// Add admin services (uses same connection as Orders module)
builder.Services.AddSingleton<IAdminReader>(sp =>
    new PostgresAdminDataAccess(
        sp.GetRequiredKeyedService<NpgsqlDataSource>(OrdersModule.ModuleKey),
        schema: "orders"));
builder.Services.AddSingleton<IAdminOperator>(sp =>
    new PostgresAdminOperator(
        sp.GetRequiredKeyedService<NpgsqlDataSource>(OrdersModule.ModuleKey),
        schema: "orders"));

// Add GraphQL with subscriptions
builder.Services
    .AddGraphQLServer()
    .AddHttpRequestInterceptor<TenantHttpRequestInterceptor>()
    .AddInstrumentation(o =>
    {
        o.RequestDetails = RequestDetails.Operation;
        o.Scopes = ActivityScopes.ExecuteHttpRequest;
        o.RenameRootActivity = true;
    })
    .AddTypes()
    .AddAlbertoAdminGraphQL()
    .AddInMemorySubscriptions();

var app = builder.Build();

// Configure pipeline
app.UseRouting();
app.UseWebSockets();
app.MapGraphQL();

app.Run();

// Make Program class accessible for testing
namespace Alberto.Orders.Api
{
    public partial class Program;
}
