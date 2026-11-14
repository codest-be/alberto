using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Alberto.Projections;

/// <summary>
/// Framework-side writer for the bookkeeping columns of <see cref="IProjectionEntity"/>.
/// The interface is read-only so user code cannot overwrite them; the framework writes through
/// the entity's own setter, which may be private or init-only (EF binds those too).
/// </summary>
internal static class ProjectionEntityStamp
{
    private static readonly ConcurrentDictionary<(Type, string), Delegate> Setters = new();

    internal static void DocumentId(IProjectionEntity e, string v) => Set(e, nameof(IProjectionEntity.DocumentId), v);
    internal static void UpdatedAt(IProjectionEntity e, DateTimeOffset v) => Set(e, nameof(IProjectionEntity.UpdatedAt), v);
    internal static void LastProcessedPosition(IProjectionEntity e, long v) => Set(e, nameof(IProjectionEntity.LastProcessedPosition), v);
    internal static void RebuildVersion(IProjectionEntity e, int v) => Set(e, nameof(IProjectionEntity.RebuildVersion), v);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2080",
        Justification = "The entity type is already rooted by the serializer or DbContext model that materialises it, which keeps these properties.")]
    private static void Set<T>(IProjectionEntity entity, string name, T value)
    {
        var type = entity.GetType();
        var setter = (Action<object, T>)Setters.GetOrAdd((type, name), static k =>
        {
            var setMethod = k.Item1.GetProperty(k.Item2, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetSetMethod(true)
                ?? throw new InvalidOperationException(
                    $"{k.Item1.Name}.{k.Item2} has no setter. Projection entities need one (it may be private or init-only) so the framework can stamp it.");
            var o = Expression.Parameter(typeof(object));
            var v = Expression.Parameter(typeof(T));
            return Expression.Lambda<Action<object, T>>(Expression.Call(Expression.Convert(o, k.Item1), setMethod, v), o, v).Compile();
        });
        setter(entity, value);
    }
}
