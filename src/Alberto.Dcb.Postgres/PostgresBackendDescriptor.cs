using Alberto.Dcb.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Alberto.Dcb.Append;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Declares the PostgreSQL event store backend for a module.
/// </summary>
/// <param name="Options">The backend's settings, before any configuration overlay.</param>
public sealed record PostgresBackendDescriptor(PostgresOptions Options) : IAlbertoBackendDescriptor
{
    /// <inheritdoc />
    public string Name => "Postgres";

    /// <inheritdoc />
    public bool SupportsTenancy => true;

    /// <inheritdoc />
    public string? StorageIdentity
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Options.ConnectionString))
                return null;

            NpgsqlConnectionStringBuilder builder;
            try
            {
                builder = new NpgsqlConnectionStringBuilder(Options.ConnectionString);
            }
            catch (ArgumentException)
            {
                // An unparseable connection string is somebody else's failure to report; this
                // property only exists to compare two descriptors.
                return null;
            }

            var schema = string.IsNullOrWhiteSpace(Options.Schema) ? "public" : Options.Schema;
            return $"{builder.Host}:{builder.Port}/{builder.Database} (schema {schema})";
        }
    }

    /// <inheritdoc />
    public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) =>
        this with
        {
            Options = AlbertoOptionsOverlay.Overlay<PostgresOptions, PostgresOverrides>(
                moduleSection, "Postgres", Options),
        };

    /// <inheritdoc />
    public IAlbertoBackendDescriptor ApplyShardConfiguration(IConfiguration shardSection)
    {
        ArgumentNullException.ThrowIfNull(shardSection);

        var overrides = shardSection.Get<PostgresOverrides>();
        return overrides is null ? this : this with { Options = overrides.ApplyTo(Options) };
    }

    /// <inheritdoc />
    public (string? SectionName, Type? OverridesType) GetConfigurationSection() =>
        ("Postgres", typeof(PostgresOverrides));

    /// <inheritdoc />
    public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition)
    {
        var path = $"{definition.ConfigurationPath}:Postgres";

        // A sharded module's own .WithPostgres(...) is a template that every shard starts from,
        // not a database anything connects to, so it is allowed to carry no connection string.
        // Each shard is validated separately and does have to have one.
        var isShardTemplate = definition.Tenancy.IsSharded && definition.ShardId is null;

        if (string.IsNullOrWhiteSpace(Options.ConnectionString) && !isShardTemplate)
        {
            yield return new AlbertoValidationFailure(
                "ALB1001",
                "The Postgres backend has no connection string.",
                definition.ShardId is null
                    ? $"Set it with .WithPostgres(o => o with {{ ConnectionString = ... }}) or '{path}:ConnectionString'."
                    : $"Set it with .AddShard(\"{definition.ShardId}\", o => o with {{ ConnectionString = ... }}) " +
                      $"or '{path.Replace(":Postgres", $":Tenancy:Shards:{definition.ShardId}", StringComparison.Ordinal)}:ConnectionString'.");
        }

        if (Options.MaxPoolSize <= 0)
        {
            yield return new AlbertoValidationFailure(
                "ALB1002",
                $"Postgres MaxPoolSize is {Options.MaxPoolSize}, which is not a positive count.",
                $"Set a positive pool size via '{path}:MaxPoolSize'.");
        }

        if (Options.MinPoolSize > Options.MaxPoolSize)
        {
            yield return new AlbertoValidationFailure(
                "ALB1003",
                $"Postgres MinPoolSize ({Options.MinPoolSize}) is larger than MaxPoolSize ({Options.MaxPoolSize}).",
                $"Lower '{path}:MinPoolSize' or raise '{path}:MaxPoolSize'.");
        }

        if (Options.LeaseDuration <= TimeSpan.Zero)
        {
            yield return new AlbertoValidationFailure(
                "ALB1004",
                $"Postgres LeaseDuration is {Options.LeaseDuration}, which is not a positive duration.",
                $"Set a positive duration via '{path}:LeaseDuration'.");
        }

        if (!string.IsNullOrWhiteSpace(Options.Schema) &&
            !SchemaQualifier.IsValidName(Options.Schema))
        {
            yield return new AlbertoValidationFailure(
                "ALB1005",
                $"Postgres schema '{Options.Schema}' is not a safe PostgreSQL identifier.",
                $"Set '{path}:Schema' to a lowercase identifier that starts with a letter and " +
                "contains only lowercase letters, digits, and underscores (maximum 63 characters).");
        }
    }

    /// <inheritdoc />
    public void RegisterShardCatalog(AlbertoModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var moduleKey = context.ModuleKey;
        var catalogKey = $"{moduleKey}:catalog";
        var runtime = new PostgresRuntimeOptions(moduleKey, Options);

        // The catalog's own data source. Not a shard's: the whole point of the control database is
        // that resolving a tenant does not depend on any one shard being reachable.
        services.AddKeyedSingleton<NpgsqlDataSource>(catalogKey, (sp, _) =>
        {
            var options = runtime.ResolveCatalog(sp);
            var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);
            builder.ConnectionStringBuilder.MaxPoolSize = options.MaxPoolSize;
            builder.ConnectionStringBuilder.MinPoolSize = options.MinPoolSize;
            return builder.Build();
        });

        services.AddKeyedSingleton<ITenantShardMap>(moduleKey, (sp, _) =>
        {
            var options = runtime.ResolveCatalog(sp);
            return new PostgresTenantShardMap(
                sp.GetRequiredKeyedService<NpgsqlDataSource>(catalogKey), moduleKey, options.Schema);
        });

        services.AddSingleton<IHostedService>(sp => new PostgresCatalogMigrationHostedService(
            moduleKey,
            sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>(),
            sp.GetService<ILogger<PostgresCatalogMigrationHostedService>>()));
    }

    /// <inheritdoc />
    public void Register(AlbertoModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var moduleKey = context.ModuleKey;

        // Every factory below resolves its options through this rather than closing over Options,
        // so a value supplied only from configuration is honoured. See PostgresRuntimeOptions.
        var runtime = new PostgresRuntimeOptions(moduleKey, Options);

        // Migration hosted service — reads options from the monitor at StartAsync so the
        // configuration overlay is applied before any connection is opened. A shard also gets the
        // module's ShardHealth, which is what turns a fatal migration failure into a degraded
        // shard the other databases can carry on without.
        var shardId = context.ShardId;
        var logicalModuleKey = context.LogicalModuleKey;
        services.AddSingleton<IHostedService>(sp => new AlbertoMigrationHostedService(
            moduleKey,
            sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>(),
            sp.GetService<ILogger<AlbertoMigrationHostedService>>(),
            shardId is null ? null : sp.GetKeyedService<ShardHealth>(logicalModuleKey),
            shardId));

        // Register NpgsqlDataSource with connection pool settings.
        services.AddKeyedSingleton<NpgsqlDataSource>(moduleKey, (sp, _) =>
        {
            var opts = runtime.Resolve(sp);
            var builder = new NpgsqlDataSourceBuilder(opts.ConnectionString);
            builder.ConnectionStringBuilder.MaxPoolSize = opts.MaxPoolSize;
            builder.ConnectionStringBuilder.MinPoolSize = opts.MinPoolSize;
            return builder.Build();
        });

        // Register append interceptor pipeline.
        services.AddKeyedSingleton<IAppendInterceptorPipeline>(moduleKey, (sp, _) =>
        {
            var interceptors = sp.GetKeyedServices<IAppendInterceptor>(moduleKey);
            return new AppendInterceptorPipeline(interceptors);
        });

        if (context.TenancyEnabled)
            PostgresBuilderExtensions.RegisterTenantBackend(context, runtime);
        else
            PostgresBuilderExtensions.RegisterSingleTenantBackend(context, runtime);

        // Checkpoint store with caching layer.
        services.AddKeyedSingleton<ICheckpointStore>(moduleKey, (sp, _) =>
        {
            var opts = runtime.Resolve(sp);
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var postgresStore = new PostgresCheckpointStore(dataSource, opts.Schema);
            return new CachingCheckpointStore(postgresStore);
        });

        // Dead letter store.
        services.AddKeyedSingleton<IDeadLetterStore>(moduleKey, (sp, _) =>
        {
            var (opts, definition) = runtime.ResolveWithDefinition(sp);
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresDeadLetterStore(
                dataSource,
                opts.Schema,
                multiTenant: definition.TenancyEnabled);
        });

        // Projection rebuild store — always registered; nothing happens until an operator
        // starts a rebuild, and a module that never calls WithRebuilds() simply has no
        // coordinator to act on it.
        services.AddKeyedSingleton<PostgresProjectionRebuildStore>(moduleKey, (sp, _) =>
        {
            var opts = runtime.Resolve(sp);
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresProjectionRebuildStore(dataSource, opts.Schema);
        });
        services.AddKeyedSingleton<IProjectionRebuildStore>(moduleKey, (sp, _) =>
            sp.GetRequiredKeyedService<PostgresProjectionRebuildStore>(moduleKey));
        services.AddKeyedSingleton<IProjectionRebuildCoordinatorStore>(moduleKey, (sp, _) =>
            sp.GetRequiredKeyedService<PostgresProjectionRebuildStore>(moduleKey));

        // Processor lock (single-leader mode — schema-less, no Options needed).
        services.AddKeyedSingleton<IProcessorLock>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresProcessorLock(dataSource);
        });

        // Tenant processor lock (used for tenant-distributed mode).
        services.AddKeyedSingleton<ITenantProcessorLock>(moduleKey, (sp, _) =>
        {
            var opts = runtime.Resolve(sp);
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresTenantProcessorLock(dataSource, opts.Schema, opts.LeaseDuration);
        });

        // Processor lease manager.
        services.AddKeyedSingleton<IProcessorLeaseManager>(moduleKey, (sp, _) =>
        {
            var opts = runtime.Resolve(sp);
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresProcessorLeaseManager(dataSource, opts.Schema, opts.LeaseDuration);
        });

        // Append signal shared by the LISTEN/NOTIFY listener and EventStoreHead.
        services.AddKeyedSingleton<IEventAppendedSignal>(moduleKey, (_, _) => new EventAppendedSignal());

        // Notify listener — always registered; EnableNotifyListener is checked at resolution
        // time from the monitor so a config-supplied false is honoured.
        services.AddSingleton<IHostedService>(sp =>
        {
            var opts = runtime.Resolve(sp);
            if (!opts.EnableNotifyListener)
                return PostgresNullHostedService.Instance;
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var signal = sp.GetRequiredKeyedService<IEventAppendedSignal>(moduleKey);
            return new PostgresEventListener(
                dataSource, opts.Schema, signal, sp.GetService<ILogger<PostgresEventListener>>());
        });
    }
}

/// <summary>
/// No-op <see cref="IHostedService"/> returned when <see cref="PostgresOptions.EnableNotifyListener"/>
/// is false. Allows the factory lambda to always register without a composition-time conditional.
/// </summary>
file sealed class PostgresNullHostedService : IHostedService
{
    public static readonly PostgresNullHostedService Instance = new();
    private PostgresNullHostedService() { }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
