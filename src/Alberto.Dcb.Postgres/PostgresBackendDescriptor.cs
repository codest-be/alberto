using Alberto.Dcb.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Alberto.Dcb.Append;
using Alberto.Dcb.Subscriptions;

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
    public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) =>
        this with
        {
            Options = AlbertoOptionsOverlay.Overlay<PostgresOptions, PostgresOverrides>(
                moduleSection, "Postgres", Options),
        };

    /// <inheritdoc />
    public (string? SectionName, Type? OverridesType) GetConfigurationSection() =>
        ("Postgres", typeof(PostgresOverrides));

    /// <inheritdoc />
    public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition)
    {
        var path = $"{definition.ConfigurationPath}:Postgres";

        if (string.IsNullOrWhiteSpace(Options.ConnectionString))
        {
            yield return new AlbertoValidationFailure(
                "ALB1001",
                "The Postgres backend has no connection string.",
                $"Set it with .WithPostgres(o => o with {{ ConnectionString = ... }}) or '{path}:ConnectionString'.");
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
    }

    /// <inheritdoc />
    public void Register(AlbertoModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var moduleKey = context.ModuleKey;

        // Migration hosted service — reads options from the monitor at StartAsync so the
        // configuration overlay is applied before any connection is opened.
        services.AddSingleton<IHostedService>(sp => new AlbertoMigrationHostedService(
            moduleKey,
            sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>(),
            sp.GetService<ILogger<AlbertoMigrationHostedService>>()));

        // Register NpgsqlDataSource with connection pool settings. The factory reads the
        // overlay-applied options via IOptionsMonitor so a connection string supplied only
        // from configuration (not from the WithPostgres callback) is honoured.
        services.AddKeyedSingleton<NpgsqlDataSource>(moduleKey, (sp, _) =>
        {
            var definition = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey);
            var opts = definition.Backend is PostgresBackendDescriptor desc ? desc.Options : Options;
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
            PostgresBuilderExtensions.RegisterTenantBackend(context, Options);
        else
            PostgresBuilderExtensions.RegisterSingleTenantBackend(context, Options);

        // Checkpoint store with caching layer.
        // Resolves PostgresOptions from IOptionsMonitor so that configuration overlays
        // applied in Phase 2 (e.g. Postgres:Schema from appsettings) are honoured here.
        services.AddKeyedSingleton<ICheckpointStore>(moduleKey, (sp, _) =>
        {
            var definition = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey);
            var opts = definition.Backend is PostgresBackendDescriptor desc ? desc.Options : Options;
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            var postgresStore = new PostgresCheckpointStore(dataSource, opts.Schema);
            return new CachingCheckpointStore(postgresStore);
        });

        // Dead letter store.
        services.AddKeyedSingleton<IDeadLetterStore>(moduleKey, (sp, _) =>
        {
            var definition = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey);
            var opts = definition.Backend is PostgresBackendDescriptor desc ? desc.Options : Options;
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresDeadLetterStore(dataSource, opts.Schema);
        });

        // Processor lock (single-leader mode — schema-less, no Options needed).
        services.AddKeyedSingleton<IProcessorLock>(moduleKey, (sp, _) =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresProcessorLock(dataSource);
        });

        // Tenant processor lock (used for tenant-distributed mode).
        services.AddKeyedSingleton<ITenantProcessorLock>(moduleKey, (sp, _) =>
        {
            var definition = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey);
            var opts = definition.Backend is PostgresBackendDescriptor desc ? desc.Options : Options;
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresTenantProcessorLock(dataSource, opts.Schema, opts.LeaseDuration);
        });

        // Processor lease manager.
        services.AddKeyedSingleton<IProcessorLeaseManager>(moduleKey, (sp, _) =>
        {
            var definition = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey);
            var opts = definition.Backend is PostgresBackendDescriptor desc ? desc.Options : Options;
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey);
            return new PostgresProcessorLeaseManager(dataSource, opts.Schema, opts.LeaseDuration);
        });

        // Append signal shared by the LISTEN/NOTIFY listener and EventStoreHead.
        services.AddKeyedSingleton<IEventAppendedSignal>(moduleKey, (_, _) => new EventAppendedSignal());

        // Notify listener — always registered; EnableNotifyListener is checked at resolution
        // time from the monitor so a config-supplied false is honoured.
        services.AddSingleton<IHostedService>(sp =>
        {
            var definition = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey);
            var opts = definition.Backend is PostgresBackendDescriptor desc ? desc.Options : Options;
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
