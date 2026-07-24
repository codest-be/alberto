using System.Text;
using System.Text.Json;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Shared read/append helpers for the single-tenant and multi-tenant PostgreSQL
/// backends. Centralising this logic ensures that the two implementations cannot
/// drift from each other on:
/// <list type="bullet">
/// <item>The 3-flag append-function-name matrix (wildcard / all-tags / intersect).</item>
/// <item>The column-ordinal snapshot and per-row event deserialisation.</item>
/// <item>The JSON serialisation of events sent to the append functions.</item>
/// </list>
/// </summary>
internal static class PostgresBackendHelpers
{
    // ---------------------------------------------------------------------------
    // Append function-name resolution
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Resolves the PostgreSQL append function name from the three query-shape flags.
    /// <para>
    /// Naming convention (applies equally to single-tenant and multi-tenant variants —
    /// the multi-tenant functions accept an additional leading <c>p_tenant_id</c>
    /// parameter but carry the same suffix):
    /// </para>
    /// <list type="bullet">
    /// <item><c>alberto_append_events</c>   — exact-tag union boundary</item>
    /// <item><c>…_v2</c>                   — wildcard-tag union boundary</item>
    /// <item><c>…_v3</c>                   — all-tags union boundary</item>
    /// <item><c>…_v4</c>                   — exact-tag intersect boundary</item>
    /// <item><c>…_v5</c>                   — wildcard-tag intersect boundary</item>
    /// <item><c>…_v6</c>                   — all-tags intersect boundary</item>
    /// </list>
    /// </summary>
    internal static string ResolveAppendFunctionName(bool useWildcard, bool useAllTags, bool useIntersect)
        => useIntersect
            ? (useAllTags ? "alberto_append_events_v6"
                : useWildcard ? "alberto_append_events_v5"
                : "alberto_append_events_v4")
            : (useAllTags ? "alberto_append_events_v3"
                : useWildcard ? "alberto_append_events_v2"
                : "alberto_append_events");

    // ---------------------------------------------------------------------------
    // Column ordinal snapshot
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Pre-resolved ordinals for an event result set, obtained once after
    /// <see cref="NpgsqlCommand.ExecuteReaderAsync(System.Threading.CancellationToken)"/> returns and before the read
    /// loop starts. Eliminates one <see cref="NpgsqlDataReader.GetOrdinal"/> string
    /// lookup per column per row.
    /// </summary>
    internal readonly struct EventColumnOrdinals
    {
        public readonly int GlobalPosition;
        /// <summary>
        /// Ordinal of the <c>tenant_id</c> column, or <c>-1</c> when the result
        /// set does not include that column (single-tenant reads and all append results).
        /// </summary>
        public readonly int TenantId;
        public readonly int EventId;
        public readonly int EventType;
        public readonly int EventTags;
        public readonly int EventData;
        public readonly int EventMetadata;
        public readonly int CreatedAt;

        internal EventColumnOrdinals(NpgsqlDataReader reader, bool includeTenantId)
        {
            GlobalPosition = reader.GetOrdinal("global_position");
            TenantId = includeTenantId ? reader.GetOrdinal("tenant_id") : -1;
            EventId = reader.GetOrdinal("event_id");
            EventType = reader.GetOrdinal("event_type");
            EventTags = reader.GetOrdinal("event_tags");
            EventData = reader.GetOrdinal("event_data");
            EventMetadata = reader.GetOrdinal("event_metadata");
            CreatedAt = reader.GetOrdinal("created_at");
        }
    }

    // ---------------------------------------------------------------------------
    // Row deserialisation
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Materialises one <see cref="IEventEnvelope"/> from the reader's current row
    /// using pre-resolved ordinals.
    /// </summary>
    /// <param name="reader">A reader positioned at a valid row.</param>
    /// <param name="ord">Ordinals resolved before the read loop.</param>
    /// <param name="tenantId">
    /// Explicit tenant id override, used by the multi-tenant append path: the
    /// append function return set does not include a <c>tenant_id</c> column, so
    /// the caller passes the tenant it is appending for.  Ignored when
    /// <see cref="EventColumnOrdinals.TenantId"/> is not <c>-1</c>.
    /// </param>
    internal static IEventEnvelope ReadEvent(
        NpgsqlDataReader reader,
        in EventColumnOrdinals ord,
        string? tenantId = null)
    {
        var globalPosition = reader.GetInt64(ord.GlobalPosition);
        var effectiveTenantId = ord.TenantId >= 0 ? reader.GetString(ord.TenantId) : tenantId;
        var eventId = reader.GetGuid(ord.EventId);
        var eventType = reader.GetString(ord.EventType);
        var eventTags = reader.GetFieldValue<string[]>(ord.EventTags);
        var eventData = reader.GetString(ord.EventData);
        var eventMetadata = reader.GetString(ord.EventMetadata);
        var createdAt = reader.GetDateTime(ord.CreatedAt);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(eventMetadata) ?? [];

        return new EventEnvelope
        {
            Id = eventId,
            TenantId = effectiveTenantId,
            GlobalPosition = globalPosition,
            EventType = new EventType(eventType),
            // EventTag.FromStorage skips the validation regex — tags stored in the
            // DB are already valid by construction (validated at write time).
            Tags = eventTags.Select(EventTag.FromStorage).ToArray(),
            EventData = eventData,
            Metadata = metadata,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
        };
    }

    // ---------------------------------------------------------------------------
    // Shared read loop
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Executes <paramref name="cmd"/>, resolves column ordinals once, and reads
    /// all rows into a list.
    /// </summary>
    /// <param name="cmd">A command that has already been fully parameterised.</param>
    /// <param name="includeTenantId">
    /// <see langword="true"/> when the result set includes a <c>tenant_id</c> column
    /// (multi-tenant stream queries); <see langword="false"/> otherwise.
    /// </param>
    /// <param name="tenantId">
    /// Passed through to <see cref="ReadEvent"/> as the override tenant id.
    /// Only relevant when <paramref name="includeTenantId"/> is <see langword="false"/>
    /// and the caller knows which tenant the rows belong to (multi-tenant append).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<List<IEventEnvelope>> ReadEventsAsync(
        NpgsqlCommand cmd,
        bool includeTenantId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var results = new List<IEventEnvelope>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ord = new EventColumnOrdinals(reader, includeTenantId);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadEvent(reader, in ord, tenantId));
        return results;
    }

    // ---------------------------------------------------------------------------
    // JSON serialisation for append
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Serialises <paramref name="events"/> to the JSON array expected by the
    /// <c>alberto_append_events*</c> PostgreSQL functions.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="Utf8JsonWriter"/> with <c>WriteRawValue</c>
    /// for the <c>event_data</c> field, which writes the caller-supplied JSON string
    /// verbatim without parsing it into an intermediate <see cref="JsonDocument"/>.
    /// This avoids the <see cref="System.Buffers.ArrayPool{T}"/> rental leak that
    /// occurred when <c>JsonDocument.Parse(…).RootElement</c> was embedded in an
    /// anonymous-object graph and passed to <see cref="System.Text.Json.JsonSerializer"/>.
    /// </remarks>
    internal static string BuildEventsJson(List<IEventToPersist> events)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartArray();
        foreach (var e in events)
        {
            writer.WriteStartObject();

            writer.WriteString("event_id", e.Id);
            writer.WriteString("event_type", e.EventType.Id);

            writer.WriteStartArray("event_tags");
            foreach (var tag in e.Tags)
                writer.WriteStringValue(tag.Value);
            writer.WriteEndArray();

            // Write raw JSON — no parse/clone cycle, no pooled buffer rental.
            writer.WritePropertyName("event_data");
            writer.WriteRawValue(e.EventData);

            writer.WriteStartObject("event_metadata");
            foreach (var kvp in e.Metadata)
                writer.WriteString(kvp.Key, kvp.Value);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
