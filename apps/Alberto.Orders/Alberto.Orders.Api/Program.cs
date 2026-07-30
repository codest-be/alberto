using Alberto.Dcb.Tenancy;
using Alberto.Orders.Api.GraphQL;
using Alberto.Orders.Platform;
using Alberto.Payments.Platform;
using HotChocolate.Diagnostics;
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

// Add GraphQL.
//
// There is no subscription backplane here because nothing in this host subscribes: the
// admin surface that did is parked on the feature/admin-surface branch, and the Orders
// and Payments slices expose queries and mutations only. Reinstating it means adding
// AddPostgresSubscriptions back alongside UseWebSockets — an in-memory topic will not
// do, since the API runs with replicas (see apps/Alberto.AppHost/Program.cs).
builder.Services
    .AddGraphQLServer()
    .AddHttpRequestInterceptor<TenantHttpRequestInterceptor>()
    .AddInstrumentation(o =>
    {
        o.RequestDetails = RequestDetails.Operation;
        o.Scopes = ActivityScopes.ExecuteHttpRequest;
        o.RenameRootActivity = true;
    })
    .AddOrdersTypes()
    .AddPaymentsTypes();

var app = builder.Build();

// Configure pipeline
app.UseRouting();
app.MapGraphQL();

await app.RunWithGraphQLCommandsAsync(args);

// Make Program class accessible for testing
namespace Alberto.Orders.Api
{
    public partial class Program;
}
