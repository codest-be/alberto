using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres").WithPgAdmin();
var database = postgres.AddDatabase("alberto-db");

var migrations = builder.AddProject<Alberto_SqlMigrator>("sql-migrator")
    .WithReference(database)
    .WaitFor(database);

builder
    .AddProject<Projects.Alberto_Example>("alberto-example")
    .WithReference(database)
    .WaitFor(migrations);

builder.Build().Run();
