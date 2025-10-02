using Projects;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres = builder.AddPostgres("postgres").WithPgAdmin();
IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("alberto-db");

IResourceBuilder<ProjectResource> migrations = builder.AddProject<Alberto_SqlMigrator>("sql-migrator")
    .WithReference(database)
    .WaitFor(database);

builder
    .AddProject<Alberto_Example>("alberto-example")
    .WithReference(database)
    .WaitFor(migrations);

builder.Build().Run();