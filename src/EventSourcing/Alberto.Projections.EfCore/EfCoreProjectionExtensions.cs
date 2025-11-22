using Alberto.EventSourcing.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Projections.EfCore;

/// <summary>
/// Extension methods for registering EF Core projection repositories.
/// </summary>
public static class EfCoreProjectionExtensions
{
    /// <summary>
    /// Registers an EF Core-backed projection repository for a specific projection type.
    /// The projection entity must implement IHasKey and IVersionedProjection.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core DbContext type containing the projection entity</typeparam>
    /// <typeparam name="TKey">The type of the projection's primary key</typeparam>
    /// <typeparam name="TState">The projection entity type</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    /// <example>
    /// <code>
    /// services.AddDbContext&lt;OrderDbContext&gt;(options => options.UseNpgsql(connectionString));
    /// services.AddEfCoreProjectionRepository&lt;OrderDbContext, Guid, OrderSummary&gt;();
    ///
    /// // In tests, swap to in-memory:
    /// services.AddSingleton&lt;IProjectionRepository&lt;Guid, OrderSummary&gt;, InMemoryProjectionRepository&lt;Guid, OrderSummary&gt;&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddEfCoreProjectionRepository<TDbContext, TKey, TState>(
        this IServiceCollection services)
        where TDbContext : DbContext
        where TState : class, IHasKey<TKey>, IVersionedProjection, new()
        where TKey : notnull
    {
        services.AddScoped<IProjectionRepository<TKey, TState>>(sp =>
        {
            var context = sp.GetRequiredService<TDbContext>();
            return new EfCoreProjectionRepository<TKey, TState>(context);
        });

        return services;
    }

    /// <summary>
    /// Helper extension to check if a projection has already processed an event (idempotency check).
    /// </summary>
    /// <typeparam name="T">The projection type implementing IVersionedProjection</typeparam>
    /// <param name="projection">The projection instance</param>
    /// <param name="globalPosition">The global position of the current event</param>
    /// <returns>True if the event has already been processed, false otherwise</returns>
    public static bool IsAlreadyProcessed<T>(this T projection, long globalPosition)
        where T : IVersionedProjection
    {
        return projection.GlobalVersion >= globalPosition;
    }
}