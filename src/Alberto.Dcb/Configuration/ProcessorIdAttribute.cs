using System.Reflection;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// Pins a processor's checkpoint key to a fixed string, independent of the type's name.
/// </summary>
/// <remarks>
/// Apply this when renaming a handler whose checkpoint must survive the rename, or when the
/// derived name would collide with another processor in the same module. Once a processor has
/// run in production its id is data: changing it restarts the processor from position zero.
/// </remarks>
/// <example>
/// <code>
/// [ProcessorId("orders.summary")]
/// public sealed class OrderSummaryReactor { }
/// </code>
/// </example>
/// <param name="id">The checkpoint key. Must be non-empty and contain no whitespace.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ProcessorIdAttribute(string id) : Attribute
{
    /// <summary>The checkpoint key this handler uses.</summary>
    public string Id { get; } = id;
}

/// <summary>
/// Derives the checkpoint key for a processor from its handler type.
/// </summary>
public static class ProcessorId
{
    /// <summary>Returns the processor id for <typeparamref name="THandler"/>.</summary>
    public static string For<THandler>() => For(typeof(THandler));

    /// <summary>
    /// Returns the processor id for <paramref name="handlerType"/>: the value of its
    /// <see cref="ProcessorIdAttribute"/> when present, otherwise the type's name qualified by
    /// any declaring types and generic arguments.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The type carries a <see cref="ProcessorIdAttribute"/> whose id is empty or contains whitespace.
    /// </exception>
    public static string For(Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);

        var attribute = handlerType.GetCustomAttribute<ProcessorIdAttribute>(inherit: false);
        if (attribute is null)
            return Describe(handlerType);

        if (string.IsNullOrWhiteSpace(attribute.Id) || attribute.Id.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"{handlerType.Name} has a [ProcessorId] attribute whose id is blank or contains whitespace. " +
                "A processor id must be non-empty and must not contain any whitespace characters.");
        }

        return attribute.Id;
    }

    private static string Describe(Type type)
    {
        var name = type.Name;

        var arity = name.IndexOf('`', StringComparison.Ordinal);
        if (arity >= 0)
            name = name[..arity];

        if (type.IsGenericType)
            name = $"{name}_{string.Join('_', type.GetGenericArguments().Select(Describe))}";

        // Only qualify by a declaring type when that declaring type is itself nested.
        // This avoids prepending the outermost enclosing class (which is usually the
        // module or test fixture, not a meaningful part of the processor identity).
        if (type.DeclaringType is { DeclaringType: not null } declaring)
            return $"{Describe(declaring)}.{name}";

        return name;
    }
}
