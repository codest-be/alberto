using System.Text.Json;

namespace Alberto.EventStore.Serialization;

/// <summary>
/// Default JSON-based event deserializer using System.Text.Json
/// </summary>
/// <remarks>
/// Default implementation, automatically registered. Users can replace via IEventDeserializer.
/// </remarks>
internal sealed class JsonEventDeserializer(JsonSerializerOptions? options = null) : IEventDeserializer
{
    private readonly JsonSerializerOptions _options = options ?? new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true
    };

    public TEvent Deserialize<TEvent>(string eventJson)
    {
        if (string.IsNullOrEmpty(eventJson))
            throw new ArgumentException("Event JSON cannot be null or empty", nameof(eventJson));

        try
        {
            var result = JsonSerializer.Deserialize<TEvent>(eventJson, _options);
            return result ??
                   throw new InvalidOperationException(
                       $"Deserialization resulted in null for type {typeof(TEvent).Name}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to deserialize event JSON to type {typeof(TEvent).Name}", ex);
        }
    }

    public object Deserialize(string eventJson, Type eventType)
    {
        if (string.IsNullOrEmpty(eventJson))
            throw new ArgumentException("Event JSON cannot be null or empty", nameof(eventJson));

        if (eventType == null)
            throw new ArgumentNullException(nameof(eventType));

        try
        {
            var result = JsonSerializer.Deserialize(eventJson, eventType, _options);
            return result ??
                   throw new InvalidOperationException($"Deserialization resulted in null for type {eventType.Name}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to deserialize event JSON to type {eventType.Name}", ex);
        }
    }
}