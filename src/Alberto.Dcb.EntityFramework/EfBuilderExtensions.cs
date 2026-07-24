using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable CS0618 // Task 12 removes DcbModuleBuilder.Services; delete this pragma then.

namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Extension methods for configuring Entity Framework with DCB modules.
/// </summary>
public static class EfBuilderExtensions
{
    /// <summary>
    /// Configures Entity Framework Core for the DCB module.
    /// Registers an IDbContextFactory for the specified DbContext type.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type for this module's projections.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="optionsAction">Action to configure the DbContext options.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithEntityFramework<TDbContext>(
        this DcbModuleBuilder builder,
        Action<DbContextOptionsBuilder> optionsAction)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsAction);

        // Register pooled DbContext factory
        builder.Services.AddPooledDbContextFactory<TDbContext>((sp, options) =>
        {
            optionsAction(options);
        });

        return builder;
    }

    /// <summary>
    /// Configures Entity Framework Core for the DCB module with access to service provider.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type for this module's projections.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="optionsAction">Action to configure the DbContext options with service provider access.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithEntityFramework<TDbContext>(
        this DcbModuleBuilder builder,
        Action<IServiceProvider, DbContextOptionsBuilder> optionsAction)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsAction);

        // Register pooled DbContext factory
        builder.Services.AddPooledDbContextFactory<TDbContext>(optionsAction);

        return builder;
    }
}
