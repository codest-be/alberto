using Projects;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres =
    builder
        .AddPostgres("postgres")
        .WithPgAdmin(o => o.WithLifetime(ContainerLifetime.Persistent).WithHostPort(54320))
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume("alberto-postgres-data")
        .WithAnnotation(new CommandLineArgsCallbackAnnotation(args =>
        {
            args.Add("-c");
            args.Add("max_connections=200");
            args.Add("-c");
            args.Add("shared_preload_libraries=pg_stat_statements");
        }));

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("alberto-db");

var api = builder
    .AddProject<Alberto_Example>("alberto-example")
    .WithReference(database)
    .WaitFor(database)
    .WithReplicas(3);

builder
    .AddNpmApp("load-tests", "../../../tst/Alberto.Example.LoadTests", "test:stress")
    .WithReference(api)
    .WithEnvironment("BASE_URL", api.GetEndpoint("http"))
    .WithExplicitStart()
    .WaitFor(api);

builder.Build().Run();