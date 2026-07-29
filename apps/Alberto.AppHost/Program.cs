using Alberto.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL with persistent volume (port 5432)
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("alberto-postgres-data")
    .WithPgAdmin(pgAdmin => pgAdmin.WithHostPort(8080));

var albertoDb = postgres.AddDatabase("alberto");

// Orders EF Migrations (runs before API starts)
var ordersMigrations = builder.AddProject<Projects.Alberto_Orders_Migrations>("orders-migrations")
    .WithReference(albertoDb)
    .WaitFor(albertoDb);

// Orders API (fixed port 5180, no proxy for direct access)
var ordersApi = builder.AddProject<Projects.Alberto_Orders_Api>("orders-api")
    .WithEndpoint("http", endpoint =>
    {
        endpoint.Port = 5180;
        endpoint.IsProxied = true;
    })
    .WithReplicas(5)
    .WithReference(albertoDb)
    .WaitFor(ordersMigrations);

// Admin Dashboard (React + urql) — served through the BFF in development.
// The BFF learns the Vite URL via WithReference(adminDashboard) which injects
// services__admin-dashboard__http__0 into the BFF process.
var adminPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../Alberto.Admin"));
var adminDashboard = builder.AddNpmApp("admin-dashboard", adminPath, "dev")
    .WithHttpEndpoint(port: 5174, env: "PORT")
    .WaitFor(ordersApi);

// Admin BFF — YARP reverse proxy + auth seam. This is the front door for the
// admin UI: it proxies /api/graphql (with WebSocket upgrade for subscriptions),
// /api/mcp (passthrough for MCP clients), and /* to the Vite dev server in
// development (or serves wwwroot in production).
var adminBff = builder.AddProject<Projects.Alberto_Admin_Bff>("admin-bff")
    .WithReference(ordersApi)
    .WithReference(adminDashboard)
    .WaitFor(ordersApi)
    .WithExternalHttpEndpoints();

// Nest the Vite dev server under the BFF in the Aspire dashboard so operators
// see the BFF as the single entry point, not the raw Vite port.
adminDashboard.WithParentRelationship(adminBff);

// K6 Load Tests (runs on-demand from dashboard)
var loadTestsPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../tests/Alberto.Orders.LoadTests"));

builder.AddK6("load-tests", loadTestsPath)
    .WaitFor(ordersApi);

builder.Build().Run();
