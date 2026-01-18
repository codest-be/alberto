using Alberto.Dcb.Admin.Internal;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Admin;

/// <summary>
/// Extension methods for adding admin panel to a DCB module.
/// </summary>
public static class AdminBuilderExtensions
{
    /// <summary>
    /// Enables the admin panel for this module.
    /// </summary>
    public static DcbModuleBuilder WithAdmin(
        this DcbModuleBuilder builder,
        Action<AdminOptions>? configure = null)
    {
        var options = new AdminOptions();
        configure?.Invoke(options);

        var moduleKey = builder.ModuleKey;

        // Store options keyed by module
        builder.Services.AddKeyedSingleton(moduleKey, options);

        // Register admin query service
        builder.Services.AddKeyedScoped<IAdminQueryService>(moduleKey, (sp, _) =>
        {
            var checkpointStore = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);
            var eventStore = sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey);
            var dataAccess = sp.GetRequiredKeyedService<IAdminDataAccess>(moduleKey);
            var adminOptions = sp.GetRequiredKeyedService<AdminOptions>(moduleKey);
            var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey);
            var consumer = sp.GetKeyedService<PollingConsumer>(moduleKey);
            var projectionClearers = sp.GetKeyedServices<IProjectionStateClearer>(moduleKey);

            return new AdminQueryService(
                moduleKey,
                checkpointStore,
                eventStore,
                dataAccess,
                adminOptions,
                deadLetterStore,
                consumer,
                projectionClearers);
        });

        // Register module for discovery
        builder.Services.Configure<AdminModuleRegistry>(registry =>
        {
            registry.RegisterModule(moduleKey, options);
        });

        return builder;
    }
}
