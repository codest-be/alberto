var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL with persistent volume
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("alberto-postgres-data")
    .WithPgAdmin();

var eventStoreDb = postgres.AddDatabase("eventstore");

builder.Build().Run();
