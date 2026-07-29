using Alberto.Dcb.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Registers the Postgres-backed admin surface for one or more Alberto modules.
/// </summary>
public static class PostgresAdminServiceCollectionExtensions
{
    /// <summary>
    /// Makes the module registered under <paramref name="moduleKey"/> operable through the
    /// admin surface, reading and writing the tables in <paramref name="schema"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this once per module a host wants operable. Each module is a separate event
    /// store: its positions, checkpoints and dead letters mean nothing next to another
    /// module's, so the admin surface addresses them one at a time rather than merging them.
    /// </para>
    /// <para>
    /// The <paramref name="isDefault"/> module is also registered as the plain, unkeyed
    /// <see cref="IAdminReader"/> and <see cref="IAdminOperator"/>. That is what the MCP
    /// tools and any other single-store consumer resolve, so adding a second module does
    /// not change what they see.
    /// </para>
    /// <para>
    /// Requires the module itself to have been registered first (<c>AddAlberto(moduleKey, …)</c>
    /// with a Postgres backend), because the reader resolves that module's keyed
    /// <see cref="NpgsqlDataSource"/>.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="moduleKey">The key the module was registered with.</param>
    /// <param name="schema">The database schema holding the module's Alberto tables.</param>
    /// <param name="isDefault">Whether this module answers when a caller names none.</param>
    public static IServiceCollection AddAlbertoPostgresAdmin(
        this IServiceCollection services,
        string moduleKey,
        string schema,
        bool isDefault = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        services.AddSingleton(new AdminModuleRegistration(
            moduleKey,
            schema,
            isDefault,
            sp => new PostgresAdminDataAccess(
                sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey), schema),
            sp => new PostgresAdminOperator(
                sp.GetRequiredKeyedService<NpgsqlDataSource>(moduleKey), schema)));

        services.TryAddSingleton<IAdminModuleRegistry>(sp =>
            new AdminModuleRegistry(sp.GetServices<AdminModuleRegistration>(), sp));

        if (isDefault)
        {
            // Resolved through the registry rather than constructed again, so the default
            // module has exactly one reader instance whichever way it is reached — the
            // topology cache inside it is then shared rather than warmed twice.
            services.AddSingleton<IAdminReader>(sp =>
                sp.GetRequiredService<IAdminModuleRegistry>().GetReader(moduleKey));
            services.AddSingleton<IAdminOperator>(sp =>
                sp.GetRequiredService<IAdminModuleRegistry>().GetOperator(moduleKey));
        }

        return services;
    }
}
