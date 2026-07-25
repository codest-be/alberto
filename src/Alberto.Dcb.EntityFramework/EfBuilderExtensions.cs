using Alberto.Dcb.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Extension methods for configuring Entity Framework with DCB modules.
/// </summary>
public static class EfBuilderExtensions
{
    /// <summary>
    /// Registers a pooled <typeparamref name="TDbContext"/> factory for this module's
    /// EF-backed projections. Takes EF's own options builder unchanged.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type for this module's projections.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">Action to configure the DbContext options.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithEntityFramework<TDbContext>(
        this DcbModuleBuilder builder,
        Action<DbContextOptionsBuilder> configure)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.Register(context =>
            context.Services.AddPooledDbContextFactory<TDbContext>(configure));
    }

    /// <summary>
    /// Registers a pooled <typeparamref name="TDbContext"/> factory whose options depend on
    /// other services.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type for this module's projections.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">Action to configure the DbContext options with service provider access.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithEntityFramework<TDbContext>(
        this DcbModuleBuilder builder,
        Action<IServiceProvider, DbContextOptionsBuilder> configure)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.Register(context =>
            context.Services.AddPooledDbContextFactory<TDbContext>(configure));
    }
}
