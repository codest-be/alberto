using Alberto.Dcb.Admin;
using Alberto.Orders.Infrastructure;
using HotChocolate.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add Aspire service defaults (telemetry, health checks, etc.)
builder.AddServiceDefaults();

// Add services
builder.Services.AddSingleton(TimeProvider.System);

// Add Orders module
builder.Services.AddOrdersModule(builder.Configuration);

// Add GraphQL
builder.Services
    .AddGraphQLServer()
    .AddInstrumentation(o =>
    {
        o.RequestDetails = RequestDetails.Operation;
        o.Scopes = ActivityScopes.ExecuteHttpRequest;
        o.RenameRootActivity = true;
    })
    .AddTypes();

var app = builder.Build();

// Configure pipeline
app.UseRouting();
app.MapGraphQL();
app.MapDcbAdmin();

app.Run();

// Make Program class accessible for testing
public partial class Program;
