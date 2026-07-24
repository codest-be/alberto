using Alberto.Dcb.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb;

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
        ArgumentNullException.ThrowIfNull(configure);

        // Phase 1 — declare. Runs the user's lambda against an accumulator; touches nothing else.
        var builder = new DcbModuleBuilder(services, moduleKey);
        configure(builder);

        var declared = builder.Definition;

        // Phase 2 — bind and validate. The definition becomes a named options instance so it can
        // be overlaid from configuration and checked by IValidateOptions under ValidateOnStart.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlbertoModuleDefinition>, AlbertoModuleValidator>());

        services.AddOptions<AlbertoModuleDefinition>(moduleKey)
            .Configure<IServiceProvider>((definition, provider) =>
            {
                var configuration = provider.GetService<IConfiguration>();
                var bound = configuration is null
                    ? declared
                    : AlbertoModuleDefinition.ApplyConfiguration(declared, configuration);

                CopyInto(bound, definition);
            })
            .ValidateOnStart();

        // Phase 3 — register. The definition is final, so nothing here is order-dependent.
        var final = declared;
        var context = new AlbertoModuleContext(services, final);

        final.Backend?.Register(context);

        foreach (var register in builder.DeferredRegistrations)
            register(context);

        return services;
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
        target.Backend = source.Backend;
        target.ControlLoop = source.ControlLoop;
        target.Telemetry = source.Telemetry;
        target.Checkpoints = source.Checkpoints;
        target.TelemetryEnabled = source.TelemetryEnabled;
        target.Processors = source.Processors;
    }
}
