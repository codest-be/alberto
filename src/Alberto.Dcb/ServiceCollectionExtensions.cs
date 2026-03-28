using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb;

/// <summary>
/// Extension methods for registering Alberto DCB modules.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds an Alberto DCB module with the specified key.
    /// Use the builder to configure the event store backend and consumers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="moduleKey">Unique key identifying this module (used for keyed service registration).</param>
    /// <param name="configure">Action to configure the module.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddAlberto("orders", builder => builder
    ///     .WithPostgres(options => options.ConnectionString = "...")
    ///     .WithControlLoop()
    /// );
    /// </code>
    /// </example>
    public static IServiceCollection AddAlberto(
        this IServiceCollection services,
        string moduleKey,
        Action<DcbModuleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(moduleKey);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new DcbModuleBuilder(services, moduleKey);
        configure(builder);
        return services;
    }
}
