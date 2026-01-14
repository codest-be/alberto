var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL with persistent volume
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("alberto-postgres-data")
    .WithPgAdmin();

var ordersDb = postgres.AddDatabase("orders");

// Orders API
builder.AddProject<Projects.Alberto_Orders_Api>("orders-api")
    .WithReference(ordersDb)
    .WaitFor(ordersDb);

builder.Build().Run();
