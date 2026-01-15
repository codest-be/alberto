var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL with persistent volume (port 5432)
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("alberto-postgres-data")
    .WithPgAdmin(pgAdmin => pgAdmin.WithHostPort(8080));

var ordersDb = postgres.AddDatabase("orders");

// Orders API (fixed port 5180, no proxy for direct access)
var ordersApi = builder.AddProject<Projects.Alberto_Orders_Api>("orders-api")
    .WithEndpoint("http", endpoint =>
    {
        endpoint.Port = 5180;
        endpoint.IsProxied = false;
    })
    .WithReference(ordersDb)
    .WaitFor(ordersDb);

// Admin Web (Angular, fixed port 4200)
builder.AddNpmApp("admin-web", "../Alberto.Admin.Web", "start")
    .WithReference(ordersApi)
    .WithHttpEndpoint(port: 4200, env: "PORT")
    .WithExternalHttpEndpoints()
    .WaitFor(ordersApi);

builder.Build().Run();
