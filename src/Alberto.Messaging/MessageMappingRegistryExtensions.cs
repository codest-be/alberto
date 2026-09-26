using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Messaging;

/// <summary>
/// Convenience extension methods for <see cref="IMessageMappingRegistry"/> that use the
/// <see cref="MessageAttribute"/> on the contract class to derive message type and version.
/// </summary>
/// <remarks>
/// Each mapping has two forms. The one that takes a <see cref="JsonTypeInfo{T}"/> serializes the
/// message with that contract — typically from a source-generated <c>JsonSerializerContext</c> —
/// and is the one to use under trimming or Native AOT. The other serializes with reflection-based
/// System.Text.Json and its default options.
/// </remarks>
public static class MessageMappingRegistryExtensions
{
    private const string ReflectionJsonMessage =
        "Serializes TMessage with reflection-based System.Text.Json. Under trimming or Native AOT, " +
        "use the Map overload that takes a JsonTypeInfo<TMessage> from a JsonSerializerContext.";

    /// <summary>
    /// Registers a mapper that projects <typeparamref name="TEvent"/> to <typeparamref name="TMessage"/>
    /// using the <see cref="MessageAttribute"/> on <typeparamref name="TMessage"/> for type and version.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public static void Map<TEvent, TMessage>(
        this IMessageMappingRegistry registry,
        Func<TEvent, TMessage> mapper)
        where TEvent : class, IEvent
        where TMessage : class
        => MapCore<TEvent, TMessage>(registry, (_, evt) => mapper(evt), static m => JsonSerializer.Serialize(m));

    /// <summary>
    /// Registers a mapper that projects <typeparamref name="TEvent"/> to <typeparamref name="TMessage"/>
    /// using the <see cref="MessageAttribute"/> on <typeparamref name="TMessage"/> for type and version,
    /// and serializes the message with <paramref name="messageTypeInfo"/>.
    /// </summary>
    public static void Map<TEvent, TMessage>(
        this IMessageMappingRegistry registry,
        Func<TEvent, TMessage> mapper,
        JsonTypeInfo<TMessage> messageTypeInfo)
        where TEvent : class, IEvent
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(messageTypeInfo);
        MapCore<TEvent, TMessage>(registry, (_, evt) => mapper(evt), m => JsonSerializer.Serialize(m, messageTypeInfo));
    }

    /// <summary>
    /// Registers a mapper that projects <typeparamref name="TEvent"/> to <typeparamref name="TMessage"/>
    /// using the <see cref="MessageAttribute"/> on <typeparamref name="TMessage"/> for type and version.
    /// <typeparamref name="TDep"/> is resolved from an event-specific dependency scope at map time.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public static void Map<TEvent, TDep, TMessage>(
        this IMessageMappingRegistry registry,
        Func<TDep, TEvent, TMessage> mapper)
        where TEvent : class, IEvent
        where TDep : notnull
        where TMessage : class
        => MapCore<TEvent, TMessage>(
            registry,
            (sp, evt) => mapper(sp.GetRequiredService<TDep>(), evt),
            static m => JsonSerializer.Serialize(m));

    /// <summary>
    /// Registers a mapper that projects <typeparamref name="TEvent"/> to <typeparamref name="TMessage"/>
    /// using the <see cref="MessageAttribute"/> on <typeparamref name="TMessage"/> for type and version,
    /// and serializes the message with <paramref name="messageTypeInfo"/>.
    /// <typeparamref name="TDep"/> is resolved from an event-specific dependency scope at map time.
    /// </summary>
    public static void Map<TEvent, TDep, TMessage>(
        this IMessageMappingRegistry registry,
        Func<TDep, TEvent, TMessage> mapper,
        JsonTypeInfo<TMessage> messageTypeInfo)
        where TEvent : class, IEvent
        where TDep : notnull
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(messageTypeInfo);
        MapCore<TEvent, TMessage>(
            registry,
            (sp, evt) => mapper(sp.GetRequiredService<TDep>(), evt),
            m => JsonSerializer.Serialize(m, messageTypeInfo));
    }

    private static void MapCore<TEvent, TMessage>(
        IMessageMappingRegistry registry,
        Func<IServiceProvider, TEvent, TMessage> mapper,
        Func<TMessage, string> serialize)
        where TEvent : class, IEvent
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(mapper);

        var attr = typeof(TMessage).GetCustomAttribute<MessageAttribute>()
            ?? throw new InvalidOperationException(
                $"Type '{typeof(TMessage).FullName}' does not have a [Message] attribute.");
        registry.Map<TEvent>((envelope, sp, _) =>
        {
            var serializer = sp.GetKeyedService<EventSerializer>(registry.ModuleKey);
            var evt = EventEnvelopeExtensions.DeserializeEvent<TEvent>(envelope, serializer);
            var message = mapper(sp, evt);
            return ValueTask.FromResult<ExternalMessage?>(
                new ExternalMessage(attr.MessageType, attr.Version.ToString(), serialize(message), []));
        });
    }
}
