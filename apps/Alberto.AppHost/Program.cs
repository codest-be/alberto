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

// K6 Load Tests (runs on-demand from dashboard)
var loadTestsPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../tests/Alberto.Orders.LoadTests"));

builder.AddK6("load-tests", loadTestsPath)
    .WaitFor(ordersApi);

builder.Build().Run();
