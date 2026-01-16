using Alberto.Dcb.Admin;
using Alberto.Dcb.Admin.Subscriptions;
using Alberto.Dcb.Tenancy;
using Alberto.Orders.Api.GraphQL;
using Alberto.Orders.Infrastructure;
using Alberto.Payments.Infrastructure;
using HotChocolate.Diagnostics;

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

// Add GraphQL with subscriptions (must be before AddAdminSubscriptions)
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
    .AddInMemorySubscriptions();

// Add admin subscriptions (real-time monitoring)
builder.Services.AddAdminSubscriptions();

var app = builder.Build();

// Configure pipeline
app.UseRouting();
app.UseWebSockets();
app.MapGraphQL();
app.MapDcbAdmin();

app.Run();

// Make Program class accessible for testing
public partial class Program;
