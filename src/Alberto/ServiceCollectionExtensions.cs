using Alberto.Configuration;
using Alberto.Subscriptions;
using Alberto.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Alberto;

/// <summary>
/// Extension methods for registering Alberto DCB modules.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds an Alberto DCB module. The <paramref name="configure"/> callback only declares
    /// intent — services are registered, and configuration is bound, after it returns. Call order
    /// inside the callback does not affect the outcome.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="moduleKey">Unique key for this module. Used as the DI service key and as the
    /// configuration path <c>Alberto:Modules:{moduleKey}</c>.</param>
    /// <param name="configure">Declares the module's backend, processors and options.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddAlberto("orders", module => module
    ///     .WithPostgres(o => o with { ConnectionString = connectionString, Schema = "orders" })
    ///     .WithControlLoop(o => o with { BatchSize = 500 }));
    /// </code>
    /// </example>
    public static IServiceCollection AddAlberto(
        this IServiceCollection services,
        string moduleKey,
        Action<DcbModuleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);

        // A module key participates in two composed DI service keys: "{moduleKey}:{processorId}"
        // (reader store factory keyed by processor) and "{moduleKey}#{shardId}" (per-shard backend
        // registration). A '#' in the module key would make ShardKey.TryParse split at the wrong
        // position and cause the shard router to resolve the wrong backend; a ':' would make the
        // reader store factory key ambiguous with Alberto's own internal suffixes (":consumer",
        // ":catalog", ":tenant-raw"). Uppercase letters, digits at the start, hyphens and other
        // punctuation make the key unsafe for use as a PostgreSQL schema name and as a metric tag.
        // Rejecting bad keys at registration time surfaces the problem at startup in development,
        // before any database connection is attempted.
        if (!IdentifierRules.IsValidIdentifier(moduleKey))
            throw new ArgumentException(
                $"Module key '{moduleKey}' is not a valid Alberto identifier. {IdentifierRules.Rule} " +
                "The key is composed into DI service keys ('{moduleKey}:{processorId}' and " +
                "'{moduleKey}#{shardId}'), so a '#' or ':' makes those keys unparseable, and " +
                "uppercase or non-alphanumeric characters make it unsafe as a PostgreSQL schema name.",
                nameof(moduleKey));

        ArgumentNullException.ThrowIfNull(configure);

        // A module key is an identity, not a name that can be reused. Registering the same one
        // twice is silently destructive rather than additive: the second call adds a second
        // Configure callback to the same named options (so the two declarations overlay each
        // other in registration order, and the later backend silently wins), and registers a
        // second set of control loops that poll the same log and race on one checkpoint row —
        // every event is dispatched twice, and neither loop's checkpoint reflects what it
        // actually processed. Refuse before anything is registered, so the first module is left
        // exactly as it was.
        if (IsModuleKeyTaken(services, moduleKey))
            throw new InvalidOperationException(
                $"ALB0026: AddAlberto has already been called with module key '{moduleKey}'. " +
                "A module key is an identity — registering it twice does not extend the first " +
                "module, it overlays its options and starts a second set of control loops that " +
                "race on the same checkpoint, dispatching every event twice." + Environment.NewLine +
                "  → Give each module its own key. To extend a module declared elsewhere, keep a " +
                "reference to its DcbModuleBuilder rather than calling AddAlberto again.");

        MarkModuleKeyTaken(services, moduleKey);

        // Phase 1 — declare. Runs the user's lambda against an accumulator; touches nothing else.
        var builder = new DcbModuleBuilder(moduleKey);
        configure(builder);

        // Tenancy is declared last, against the completed declaration, so a shard can inherit
        // from a .WithPostgres(...) written after .WithTenancy(...) in the chain.
        builder.ApplyTenancy();

        // Auto-add the control loop with defaults if the user never called WithControlLoop.
        // Done as a deferred registration so nothing resolves a service during composition.
        if (!builder.ControlLoopConfigured)
        {
            builder.ControlLoopConfigured = true;
            builder.Register(ControlLoopRegistration.Register);
        }

        var declared = builder.Definition;

        // Phase 2 — bind and validate. The definition becomes a named options instance so it can
        // be overlaid from configuration and checked by IValidateOptions under ValidateOnStart.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlbertoModuleDefinition>, AlbertoModuleValidator>());

        BindDefinition(services, moduleKey, declared, shardId: null);

        // Phase 3 — register. `declared` is the code-configured definition.
        // Configuration overlay is applied later inside the Options callback above,
        // when IOptionsMonitor resolves the named instance at host startup.
        if (!declared.Tenancy.IsSharded)
        {
            RegisterModule(services, declared, builder.DeferredRegistrations);
            return services;
        }

        // A sharded module registers everything once per shard, under the shard's own key. The
        // logical key gets no backend of its own — only the router, which forwards to whichever
        // shard the current tenant belongs to.
        foreach (var shard in declared.Tenancy.Shards)
        {
            var shardDefinition = ShardExpansion.ForShard(declared, shard);
            BindDefinition(services, shardDefinition.ServiceKey, declared, shard.ShardId);
            RegisterModule(services, shardDefinition, builder.DeferredRegistrations);
        }

        ShardingRegistration.Register(services, declared);

        return services;
    }

    /// <summary>
    /// Records that a module key has been registered. Nothing resolves this — it exists only so
    /// that a second <c>AddAlberto</c> with the same key can be detected in the descriptor list.
    /// </summary>
    /// <remarks>
    /// The named <see cref="AlbertoModuleDefinition"/> options instance cannot serve as the marker:
    /// <c>AddOptions</c> registers descriptors for the unnamed options infrastructure too, so its
    /// presence says nothing about which names were used. A dedicated marker keyed by module key
    /// makes the question exact.
    /// </remarks>
    private sealed class ModuleKeyMarker;

    /// <summary>
    /// Whether <c>AddAlberto</c> has already registered <paramref name="moduleKey"/> into this
    /// collection.
    /// </summary>
    /// <remarks>
    /// Only <see cref="ServiceDescriptor.ServiceType"/> and <see cref="ServiceDescriptor.ServiceKey"/>
    /// are read. Both are safe on every descriptor, unlike <c>ImplementationInstance</c> and
    /// <c>ImplementationType</c>, which throw when read from a keyed one — and a collection this
    /// method is handed will generally contain keyed descriptors from elsewhere in the application.
    /// </remarks>
    private static bool IsModuleKeyTaken(IServiceCollection services, string moduleKey)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(ModuleKeyMarker)
                && string.Equals(descriptor.ServiceKey as string, moduleKey, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void MarkModuleKeyTaken(IServiceCollection services, string moduleKey) =>
        services.AddKeyedSingleton(moduleKey, new ModuleKeyMarker());

    /// <summary>
    /// Registers the named <see cref="AlbertoModuleDefinition"/> options instance that every one
    /// of this module's services reads its settings from at resolution time.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">
    /// The options name, which is also the DI service key: the module key, or <c>module#shard</c>.
    /// </param>
    /// <param name="declared">The logical, code-configured declaration.</param>
    /// <param name="shardId">
    /// The shard this instance describes, or null for the module as a whole. Shard instances bind
    /// from the module's own configuration path and then narrow to their shard, so a shard added
    /// or retuned purely in configuration is picked up here.
    /// </param>
    private static void BindDefinition(
        IServiceCollection services,
        string name,
        AlbertoModuleDefinition declared,
        string? shardId)
    {
        services.AddOptions<AlbertoModuleDefinition>(name)
            .Configure<IServiceProvider>((definition, provider) =>
            {
                var configuration = provider.GetService<IConfiguration>();
                var bound = configuration is null
                    ? declared
                    : AlbertoModuleDefinition.ApplyConfiguration(declared, configuration);

                var environment = provider.GetService<IHostEnvironment>();
                if (environment is not null && !environment.IsDevelopment()
                    && bound.Checkpoints.OrphanPolicy == OrphanCheckpointPolicy.Warn)
                {
                    // Only escalate when configuration did not explicitly choose Warn.
                    // An operator who writes OrphanPolicy = Warn in appsettings.Production.json
                    // is making a deliberate choice and must not be overridden.
                    var orphanPolicySection = configuration?.GetSection(
                        $"{declared.ConfigurationPath}:Checkpoints:OrphanPolicy");
                    if (orphanPolicySection?.Exists() != true)
                    {
                        bound = bound with
                        {
                            Checkpoints = bound.Checkpoints with { OrphanPolicy = OrphanCheckpointPolicy.Strict },
                        };
                    }
                }

                if (shardId is not null)
                {
                    // Null when configuration removed the shard. Its services are still
                    // registered — the collection was built before configuration was read — so
                    // leave the instance at its declared defaults rather than throwing here;
                    // ALB0015 reports the mismatch with a message that names the shard.
                    bound = ShardExpansion.ForShard(bound, shardId) ?? bound;
                }

                CopyInto(bound, definition);
            })
            .ValidateOnStart();
    }

    /// <summary>
    /// Replays one module's declarations against a single context. Called once for an ordinary
    /// module, and once per shard for a sharded one.
    /// </summary>
    private static void RegisterModule(
        IServiceCollection services,
        AlbertoModuleDefinition definition,
        IReadOnlyList<Action<AlbertoModuleContext>> deferredRegistrations)
    {
        var serviceKey = definition.ServiceKey;
        var context = new AlbertoModuleContext(services, definition);

        definition.Backend?.Register(context);

        services.AddSingleton<IHostedService>(sp => new OrphanCheckpointHostedService(
            sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(serviceKey),
            sp.GetKeyedService<ICheckpointStore>(serviceKey) is CachingCheckpointStore caching ? caching.AsInventory : sp.GetKeyedService<ICheckpointStore>(serviceKey) as ICheckpointInventory,
            sp.GetService<ILogger<OrphanCheckpointHostedService>>()
                ?? NullLogger<OrphanCheckpointHostedService>.Instance));

        foreach (var register in deferredRegistrations)
            register(context);
    }

    /// <summary>
    /// The options pattern hands us a pre-constructed instance to populate, but
    /// <see cref="AlbertoModuleDefinition"/> is a record built by <c>with</c> expressions.
    /// This copies the computed record onto that instance.
    /// </summary>
    private static void CopyInto(AlbertoModuleDefinition source, AlbertoModuleDefinition target)
    {
        target.ModuleKey = source.ModuleKey;
        target.TenancyEnabled = source.TenancyEnabled;
        target.Tenancy = source.Tenancy;
        target.ShardId = source.ShardId;
        target.Backend = source.Backend;
        target.ControlLoop = source.ControlLoop;
        target.Telemetry = source.Telemetry;
        target.Checkpoints = source.Checkpoints;
        target.TelemetryEnabled = source.TelemetryEnabled;
        target.Processors = source.Processors;
        target.UnknownConfigurationKeys = source.UnknownConfigurationKeys;
        target.UpcasterDeclarations = source.UpcasterDeclarations;
        target.RegisteredEventTypes = source.RegisteredEventTypes;
    }
}
