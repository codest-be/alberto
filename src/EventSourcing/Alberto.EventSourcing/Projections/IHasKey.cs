namespace Alberto.EventSourcing.Projections;

/// <summary>
/// Marker interface for projection entities that have a strongly-typed key.
/// Used by EfCoreProjectionRepository to identify entities by their key.
/// </summary>
/// <typeparam name="TKey">The type of the entity's primary key</typeparam>
public interface IHasKey<out TKey>
{
    /// <summary>
    /// The primary key of the projection entity.
    /// </summary>
    TKey Id { get; }
}