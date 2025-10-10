using Projects;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres =
    builder
        .AddPostgres("postgres")
        .WithPgAdmin(o => o.WithLifetime(ContainerLifetime.Persistent).WithHostPort(54320))
        .WithLifetime(ContainerLifetime.Persistent);

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("alberto-db");

builder
    .AddProject<Alberto_Example>("alberto-example")
    .WithReference(database)
    .WaitFor(database);

builder.Build().Run();