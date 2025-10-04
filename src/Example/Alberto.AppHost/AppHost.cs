using Projects;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres =
    builder
        .AddPostgres("postgres")
        .WithPgAdmin(o => o.WithLifetime(ContainerLifetime.Persistent))
        .WithLifetime(ContainerLifetime.Persistent);

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("alberto-db");

IResourceBuilder<ProjectResource> migrations = builder.AddProject<Alberto_SqlMigrator>("sql-migrator")
    .WithReference(database)
    .WaitFor(database);

builder
    .AddProject<Alberto_Example>("alberto-example")
    .WithReference(database)
    .WaitForCompletion(migrations);

builder.Build().Run();