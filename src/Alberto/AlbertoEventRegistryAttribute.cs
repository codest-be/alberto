namespace Alberto;

/// <summary>
/// Asks the Alberto source generator to emit a <c>Registry</c> property on the marked
/// <c>static partial</c> class: an <see cref="IEventTypeRegistry"/> holding every
/// <see cref="EventTypeAttribute"/>-marked <see cref="IEvent"/> in the assembly, with tags read by
/// generated code and JSON contracts taken from <see cref="JsonContext"/>. It is the registry
/// <see cref="EventTypeRegistry.FromAssembly"/> would build, without reflection, so it is the one
/// to use under trimming or Native AOT.
/// </summary>
/// <example>
/// <code>
/// [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
/// [JsonSerializable(typeof(OrderPlaced))]
/// internal partial class OrdersJsonContext : JsonSerializerContext;
///
/// [AlbertoEventRegistry(typeof(OrdersJsonContext))]
/// public static partial class OrdersEvents;
///
/// services.AddAlberto("orders", b =&gt; b.WithPostgres(...).WithEvents(OrdersEvents.Registry));
/// </code>
/// </example>
/// <remarks>
/// Every event type must be listed on the context with <c>[JsonSerializable]</c>; the generator
/// reports one that is not (ALB3002). <c>PropertyNameCaseInsensitive = true</c> matches the options
/// the reflection path reads with.
/// </remarks>
/// <param name="jsonContext">The <c>JsonSerializerContext</c> the registry takes each event's contract from.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AlbertoEventRegistryAttribute(Type jsonContext) : Attribute
{
    /// <summary>The <c>JsonSerializerContext</c> the registry takes each event's contract from.</summary>
    public Type JsonContext { get; } = jsonContext;
}
