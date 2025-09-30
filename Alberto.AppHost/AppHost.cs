var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var database = postgres.AddDatabase("eventstore-db");

builder
    .AddProject<Projects.EventStore_Example>("eventstore-example")
    .WithReference(database);

builder.Build().Run();
