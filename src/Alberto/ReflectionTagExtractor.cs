using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Alberto;

/// <summary>
/// Builds an <see cref="EventTagExtractor"/> from the <see cref="TagAttribute"/>-marked public
/// properties of an event type: one compiled getter per property, built once per type.
/// </summary>
/// <remarks>
/// This is the reflection path. It is trim-safe because every <see cref="IEvent"/> keeps its public
/// properties (the interface carries <see cref="DynamicallyAccessedMembersAttribute"/>), but under
/// Native AOT <see cref="Expression{TDelegate}.Compile()"/> falls back to the interpreter, which is
/// slow on the append path. A hand-written or generated registry supplies its own extractor.
/// </remarks>
internal static class ReflectionTagExtractor
{
    private static readonly ConcurrentDictionary<Type, EventTagExtractor> Cache = new();

    public static EventTagExtractor For(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
        => Cache.TryGetValue(type, out var cached) ? cached : Cache.GetOrAdd(type, Build(type));

    private static EventTagExtractor Build(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
    {
        var objParam = Expression.Parameter(typeof(object), "obj");
        var cast = Expression.Convert(objParam, type);

        var getters = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (prop: p, attr: p.GetCustomAttribute<TagAttribute>()))
            .Where(x => x.attr is not null)
            .Select(x =>
            {
                // Build (object obj) => (object?)((T)obj).Prop
                // Expression.Convert to typeof(object) emits a box instruction for value types.
                var propExpr = Expression.Property(cast, x.prop);
                var boxed = Expression.Convert(propExpr, typeof(object));
                var getter = Expression.Lambda<Func<object, object?>>(boxed, objParam).Compile();
                return (Concept: x.attr!.Concept, Getter: getter);
            })
            .ToArray();

        if (getters.Length == 0)
            return static _ => [];

        return @event =>
        {
            var values = new KeyValuePair<string, object?>[getters.Length];
            for (var i = 0; i < getters.Length; i++)
                values[i] = new(getters[i].Concept, getters[i].Getter(@event));
            return values;
        };
    }
}
