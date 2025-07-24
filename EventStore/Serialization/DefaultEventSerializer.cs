using System.Text.Json;

namespace EventStore.Serialization;

internal sealed class DefaultEventSerializer : IEventSerializer
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new JsonSerializerOptions();

    public string Serialize<T>(T o) => JsonSerializer.Serialize(o, JsonSerializerOptions);

    public T? Deserialize<T>(string s) => JsonSerializer.Deserialize<T>(s, JsonSerializerOptions);

    public static object? Deserialize(Type type, string s) =>
        JsonSerializer.Deserialize(s, type, JsonSerializerOptions);
}