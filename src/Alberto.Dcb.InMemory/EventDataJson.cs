using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// Rewrites an event payload the way the PostgreSQL backend's <c>jsonb</c> column does, so the
/// in-memory backend is not a more permissive store than the one it stands in for.
/// </summary>
/// <remarks>
/// <para>
/// <c>jsonb</c> does not store the text it was given. It parses to a tree and re-emits from it, so
/// insignificant whitespace disappears, duplicate object keys collapse to the last one, and keys
/// come back sorted by length and then bytewise — <c>{"orderId":"abc", "z":1}</c> is stored and
/// returned as <c>{"z": 1, "orderId": "abc"}</c>. Keeping the caller's string verbatim, as the
/// obvious in-memory implementation does, makes a test that compares <c>EventData</c> as a string
/// pass in memory and fail against PostgreSQL.
/// </para>
/// <para>
/// So this normalizes the same structure — sorted keys, collapsed duplicates, no whitespace — and
/// deliberately <em>does not</em> reproduce jsonb's spacing (<c>", "</c> between members) or its
/// numeric canonicalization (<c>1E+30</c> is re-emitted by PostgreSQL as thirty zeros, from
/// <c>numeric</c>'s own text format). Matching those too would make the output byte-identical to
/// one PostgreSQL version's, which is a promise Alberto should not make on behalf of every
/// backend — and the numeric cases are exactly where a near-miss emulation would silently diverge.
/// The contract on <see cref="IEventEnvelope.EventData"/> is that what comes back is JSON
/// semantically equal to what went in. Normalizing here means a test that assumes more than that
/// fails immediately, in memory, rather than later against a real database.
/// </para>
/// </remarks>
internal static class EventDataJson
{
    /// <summary>
    /// Validates <paramref name="eventData"/> as JSON and returns it in canonical form.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The payload is not well-formed JSON, or contains a NUL character in a string — PostgreSQL
    /// accepts neither into a <c>jsonb</c> column, so neither does this.
    /// </exception>
    public static string Normalize(string eventData, string eventTypeId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(eventData);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"EventData for '{eventTypeId}' is not well-formed JSON, and would be rejected by " +
                $"the jsonb column the PostgreSQL backend stores it in: {ex.Message}",
                nameof(eventData), ex);
        }

        using (document)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                // The default encoder escapes non-ASCII; jsonb stores it literally. Matching that
                // keeps a payload with accents or emoji comparable across the two backends.
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                Write(document.RootElement, writer, eventTypeId);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    /// <summary>
    /// Throws if <paramref name="value"/> contains a NUL, naming <paramref name="what"/> in the
    /// message. Metadata is stored in a jsonb column too, so it is subject to the same rule.
    /// </summary>
    public static void RejectNul(string value, string what, string eventTypeId)
    {
        if (value.IndexOf('\0') < 0)
            return;

        throw new ArgumentException(
            $"{what} for '{eventTypeId}' contains a NUL character (U+0000). PostgreSQL cannot " +
            "store one in a jsonb value — the escape \\u0000 has no representation in its text " +
            "type — so an append that succeeds here would fail against the PostgreSQL backend.");
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer, string eventTypeId)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in Canonicalize(element))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer, eventTypeId);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(item, writer, eventTypeId);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var text = element.GetString()!;
                RejectNul(text, "EventData", eventTypeId);
                writer.WriteStringValue(text);
                break;

            default:
                // Numbers keep their literal text: see the class remarks on why emulating
                // PostgreSQL's numeric output is deliberately out of scope. True/false/null are
                // written by the same path, which is why this is WriteRawValue rather than a
                // per-kind switch.
                writer.WriteRawValue(element.GetRawText());
                break;
        }
    }

    /// <summary>
    /// The object's members in jsonb order, with duplicate keys collapsed to the last occurrence.
    /// </summary>
    /// <remarks>
    /// jsonb orders members by the UTF-8 <em>byte</em> length of the key and then by the bytes
    /// themselves, so the comparison is done on encoded keys rather than on .NET strings: UTF-16
    /// length and ordinal string order both disagree with that once a key leaves ASCII.
    /// </remarks>
    private static IEnumerable<JsonProperty> Canonicalize(JsonElement obj)
    {
        // Last-wins on duplicates: the indexer assignment overwrites, and the source enumerates
        // in document order.
        var members = new Dictionary<string, JsonProperty>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
            members[property.Name] = property;

        return members.Values
            .Select(p => (Property: p, Key: Encoding.UTF8.GetBytes(p.Name)))
            .OrderBy(x => x, JsonbKeyOrder.Instance)
            .Select(x => x.Property);
    }

    private sealed class JsonbKeyOrder : IComparer<(JsonProperty Property, byte[] Key)>
    {
        public static readonly JsonbKeyOrder Instance = new();

        public int Compare((JsonProperty Property, byte[] Key) x, (JsonProperty Property, byte[] Key) y)
        {
            var byLength = x.Key.Length.CompareTo(y.Key.Length);
            return byLength != 0 ? byLength : x.Key.AsSpan().SequenceCompareTo(y.Key);
        }
    }
}
