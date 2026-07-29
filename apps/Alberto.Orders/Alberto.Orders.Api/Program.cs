using Alberto.Dcb.Admin;
using Alberto.Dcb.Admin.GraphQL;
using Alberto.Dcb.Admin.Mcp;
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

// When running schema export commands there is no live database, so supply a dummy
// connection string to satisfy the module registrations.
if (args is ["schema", ..])
    builder.Configuration["ConnectionStrings:alberto"] =
        "Host=localhost;Database=schema_export;Username=nobody;Password=nobody";

// Add Orders module
builder.Services.AddOrdersModule(builder.Configuration);

// Add Payments module
builder.Services.AddPaymentsModule(builder.Configuration);

// Add the admin surface for every module this process hosts.
//
// Both modules are registered because both are real event stores with their own schema,
// checkpoints and dead letters. Registering only Orders — as this did — did not make
// Payments unsupported, it made it invisible: the console showed Orders' numbers under
// no particular label, and a Payments projection could fall arbitrarily far behind
// without anything on screen changing. Orders stays the default so a caller that names
// no module, including the MCP tools, keeps seeing exactly what it saw before.
builder.Services.AddAlbertoPostgresAdmin(OrdersModule.ModuleKey, schema: "orders", isDefault: true);
builder.Services.AddAlbertoPostgresAdmin(PaymentsModule.ModuleKey, schema: "payments");

// Add the admin MCP surface — the same IAdminReader/IAdminOperator the GraphQL
// admin types consume, exposed as tools over stateless Streamable HTTP.
builder.Services.AddAlbertoAdminMcp();

// Add GraphQL with subscriptions.
//
// The subscription backplane is Postgres LISTEN/NOTIFY, not in-process. The API runs
// with replicas (see apps/Alberto.AppHost/Program.cs), and the admin dashboard reaches
// it through a load-balancing BFF: a mutation handled by one replica must reach a
// subscriber connected to any other, which an in-memory topic cannot do.
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
    .AddPostgresSubscriptions((sp, options) =>
    {
        var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(OrdersModule.ModuleKey);
        options.ConnectionFactory = ct => dataSource.OpenConnectionAsync(ct);
    });

var app = builder.Build();

// Configure pipeline
app.UseRouting();
app.UseWebSockets();
app.MapGraphQL();
app.MapAlbertoAdminMcp("/mcp");

await app.RunWithGraphQLCommandsAsync(args);

// Make Program class accessible for testing
namespace Alberto.Orders.Api
{
    public partial class Program;
}
