using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// Convenience extension methods for <see cref="IMessageMappingRegistry"/> that use the
/// <see cref="MessageAttribute"/> on the contract class to derive message type and version.
/// </summary>
public static class MessageMappingRegistryExtensions
{
    /// <summary>
    /// Registers a mapper that projects <typeparamref name="TEvent"/> to <typeparamref name="TMessage"/>
    /// using the <see cref="MessageAttribute"/> on <typeparamref name="TMessage"/> for type and version.
    /// </summary>
    public static void Map<TEvent, TMessage>(
        this IMessageMappingRegistry registry,
        Func<TEvent, TMessage> mapper)
        where TEvent : class, IEvent
        where TMessage : class
    {
        var attr = typeof(TMessage).GetCustomAttribute<MessageAttribute>()
            ?? throw new InvalidOperationException(
                $"Type '{typeof(TMessage).FullName}' does not have a [Message] attribute.");
        registry.Map<TEvent>((envelope, _, _) =>
        {
            var evt = JsonSerializer.Deserialize<TEvent>(envelope.EventData)!;
            var message = mapper(evt);
            return ValueTask.FromResult<ExternalMessage?>(
                new ExternalMessage(attr.MessageType, attr.Version.ToString(), JsonSerializer.Serialize(message), []));
        });
    }

    /// <summary>
    /// Registers a mapper that projects <typeparamref name="TEvent"/> to <typeparamref name="TMessage"/>
    /// using the <see cref="MessageAttribute"/> on <typeparamref name="TMessage"/> for type and version.
    /// <typeparamref name="TDep"/> is resolved from the <see cref="IServiceProvider"/> at map time,
    /// mirroring the <c>ReactTo&lt;TEvent, TDep&gt;</c> pattern.
    /// </summary>
    public static void Map<TEvent, TDep, TMessage>(
        this IMessageMappingRegistry registry,
        Func<TDep, TEvent, TMessage> mapper)
        where TEvent : class, IEvent
        where TDep : notnull
        where TMessage : class
    {
        var attr = typeof(TMessage).GetCustomAttribute<MessageAttribute>()
            ?? throw new InvalidOperationException(
                $"Type '{typeof(TMessage).FullName}' does not have a [Message] attribute.");
        registry.Map<TEvent>((envelope, sp, _) =>
        {
            var dep = sp.GetRequiredService<TDep>();
            var evt = JsonSerializer.Deserialize<TEvent>(envelope.EventData)!;
            var message = mapper(dep, evt);
            return ValueTask.FromResult<ExternalMessage?>(
                new ExternalMessage(attr.MessageType, attr.Version.ToString(), JsonSerializer.Serialize(message), []));
        });
    }
}
