using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Shared read/append helpers for the single-tenant and multi-tenant PostgreSQL
/// backends. Centralising this logic ensures that the two implementations cannot
/// drift from each other on:
/// <list type="bullet">
/// <item>The stream query-shape decision tree — 11 branches selecting one <c>alberto_read_*</c>
/// function (tenancy is a parameter, not a fork). Branch order is load-bearing: the
/// <c>RequiresAllTags</c> family must be tested before the wildcard family and before the
/// plain-exact fallthrough, or an all-tags query with types selects the wrong function.</item>
/// <item>The 3-flag append-function-name matrix (wildcard / all-tags / intersect).</item>
/// <item>The full append path: lock acquisition, SQL, parameter binding, conflict catch.</item>
/// <item>The positions and stable-head barrier queries (identical SQL for both paths).</item>
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
    /// <item><c>alberto_append_events</c>   — any-tag union boundary</item>
    /// <item><c>…_v3</c>                   — all-tags union boundary</item>
    /// <item><c>…_v4</c>                   — any-tag intersect boundary</item>
    /// <item><c>…_v6</c>                   — all-tags intersect boundary</item>
    /// </list>
    /// <para>
    /// <c>_v2</c> and <c>_v5</c> were the wildcard-boundary variants and are gone; migration 024
    /// drops them. The numbering keeps its gaps rather than renumbering, so a version in a log
    /// line or an old plan still means what it meant.
    /// </para>
    /// </summary>
    internal static string ResolveAppendFunctionName(bool useAllTags, bool useIntersect)
        => useIntersect
            ? (useAllTags ? "alberto_append_events_v6" : "alberto_append_events_v4")
            : (useAllTags ? "alberto_append_events_v3" : "alberto_append_events");

    // ---------------------------------------------------------------------------
    // Stream query builder — the single decision tree for both tenancy paths
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Builds the SQL for a stream query. The 9-branch decision tree exists exactly
    /// once here; callers differ only in the <paramref name="tenanted"/> flag.
    /// <para>
    /// When <paramref name="tenanted"/> is <see langword="true"/>, <c>@p_tenant_id</c>
    /// is prepended as the first argument to every function call. Function names are
    /// shared between the tenanted and untenanted paths — only the parameter list
    /// changes. (The all-tenants stream, <c>alberto_read_all_global</c>, is a separate
    /// one-liner on <see cref="PostgresTenantEventStoreBackend"/> and is not routed
    /// through this tree.)
    /// </para>
    /// </summary>
    internal static string BuildStreamQuery(SchemaQualifier schema, DcbQuery query, bool tenanted)
    {
        var t = tenanted ? "@p_tenant_id, " : "";

        if (query.IsEmpty)
            return $"SELECT * FROM {schema.Function("alberto_read_all")}({t}@p_after_position, @p_limit)";

        if (query.HasTypesOnly)
            return $"SELECT * FROM {schema.Function("alberto_read_by_types")}({t}@p_types, @p_after_position, @p_limit)";

        if (query.RequiresAllTags)
        {
            if (query.HasTagsOnly)
                return $"SELECT * FROM {schema.Function("alberto_read_by_all_tags")}({t}@p_tags, @p_after_position, @p_limit)";

            if (query.IntersectsTypesAndTags)
                return $"SELECT * FROM {schema.Function("alberto_read_by_types_and_all_tags")}({t}@p_types, @p_tags, @p_after_position, @p_limit)";

            return $"SELECT * FROM {schema.Function("alberto_read_by_types_or_all_tags")}({t}@p_types, @p_tags, @p_after_position, @p_limit)";
        }

        if (query.HasTagsOnly)
            return $"SELECT * FROM {schema.Function("alberto_read_by_tags")}({t}@p_tags, @p_after_position, @p_limit)";

        if (query.IntersectsTypesAndTags)
            return $"SELECT * FROM {schema.Function("alberto_read_by_types_and_tags")}({t}@p_types, @p_tags, @p_after_position, @p_limit)";

        return $"SELECT * FROM {schema.Function("alberto_read_by_types_or_tags")}({t}@p_types, @p_tags, @p_after_position, @p_limit)";
    }

    // ---------------------------------------------------------------------------
    // Stream parameter binder
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Adds all parameters for a stream query command. When <paramref name="tenantId"/>
    /// is non-null, <c>p_tenant_id</c> is added first. The remaining parameters are
    /// identical for both the tenanted and untenanted paths.
    /// </summary>
    internal static void AddStreamParameters(
        NpgsqlCommand cmd, DcbQuery query, string? tenantId, long afterPosition, int? limit)
    {
        if (tenantId is not null)
            cmd.Parameters.AddWithValue("p_tenant_id", tenantId);

        cmd.Parameters.AddWithValue("p_after_position", afterPosition);
        cmd.Parameters.AddWithValue("p_limit", limit.HasValue ? limit.Value : DBNull.Value);

        if (query.Types.Count > 0)
            cmd.Parameters.AddWithValue("p_types", query.Types.Select(t => t.Id).ToArray());

        if (query.Tags.Count > 0)
            cmd.Parameters.AddWithValue("p_tags", query.Tags.Select(t => t.Value).ToArray());
    }

    // ---------------------------------------------------------------------------
    // Append SQL builder
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Builds the SQL for an append function call. When <paramref name="tenanted"/>
    /// is <see langword="true"/>, <c>@p_tenant_id</c> is prepended as the first argument.
    /// </summary>
    internal static string BuildAppendSql(
        SchemaQualifier schema, string functionName, bool tenanted, bool useAllTags)
    {
        var t = tenanted ? "@p_tenant_id, " : "";
        return useAllTags
            ? $"SELECT * FROM {schema.Function(functionName)}({t}@p_events, @p_dcb_types, @p_dcb_all_tags, @p_expected_position)"
            : $"SELECT * FROM {schema.Function(functionName)}({t}@p_events, @p_dcb_types, @p_dcb_tags, @p_expected_position)";
    }

    // ---------------------------------------------------------------------------
    // Advisory-lock acquisition
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Acquires a transaction-scoped advisory lock keyed by <paramref name="lockKey"/>.
    /// <para>
    /// The two-argument advisory-lock form (classid 1 = append serialization) occupies
    /// a distinct namespace from the single-argument processor-leadership locks, so the
    /// two families never collide. The lock is held until the transaction commits or rolls
    /// back.
    /// </para>
    /// </summary>
    internal static async Task AcquireAppendLockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string lockKey, CancellationToken cancellationToken)
    {
        await using var lockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(1, hashtext(@lock_key))", connection, transaction);
        lockCmd.Parameters.AddWithValue("lock_key", lockKey);
        await lockCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------
    // Shared append path (lock → SQL → parameters → execute → read)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Full append path: acquires the advisory lock, builds and executes the append
    /// SQL, and returns the resulting event envelopes.
    /// <para>
    /// <paramref name="lockKey"/> must be pre-computed by the caller — single-tenant
    /// uses <c>alberto-append:{schema}</c>; multi-tenant uses
    /// <c>alberto-append:{schema}:{tenantId}</c> so concurrent appends to different
    /// tenants proceed in parallel.
    /// </para>
    /// <para>
    /// When <paramref name="tenantId"/> is non-null the call is in multi-tenant mode:
    /// <c>@p_tenant_id</c> is added as the first SQL parameter and forwarded to
    /// <see cref="ReadEvent"/> so the returned envelopes carry the correct tenant.
    /// When <paramref name="tenantId"/> is null the single-tenant path is used.
    /// </para>
    /// <para>
    /// When <paramref name="transaction"/> is null a new transaction is opened and
    /// owned by this method; otherwise the supplied transaction is used and the caller
    /// is responsible for committing or rolling back.
    /// </para>
    /// </summary>
    internal static async Task<IReadOnlyCollection<IEventEnvelope>> AppendCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SchemaQualifier schema,
        string lockKey,
        string? tenantId,
        List<IEventToPersist> eventsList,
        DcbQuery? dcbQuery,
        long? expectedPosition,
        CancellationToken cancellationToken)
    {
        var useAllTagsFunction = dcbQuery?.RequiresAllTags == true;
        var useIntersect = dcbQuery?.IntersectsTypesAndTags == true;

        // Resolve the append function name from the 2-flag matrix via the shared
        // helper — single-tenant and multi-tenant use the same name set so they
        // cannot drift independently.
        var functionName = ResolveAppendFunctionName(
            useAllTags: useAllTagsFunction, useIntersect: useIntersect);

        var sql = BuildAppendSql(schema, functionName, tenanted: tenantId is not null, useAllTagsFunction);

        // #1 Write-skew fix: serialize appends for this (schema[, tenant]) with a
        // transaction-scoped advisory lock so the DCB conflict-check inside the
        // append function and the subsequent insert happen atomically. Without it,
        // two concurrent appends can both pass the "no event after expectedPosition"
        // check and both commit, violating the boundary. Lock is released on
        // commit/rollback.
        NpgsqlTransaction? ownedTransaction =
            transaction is null ? await connection.BeginTransactionAsync(cancellationToken) : null;
        var effectiveTransaction = transaction ?? ownedTransaction!;

        try
        {
            await AcquireAppendLockAsync(connection, effectiveTransaction, lockKey, cancellationToken);

            var results = await ExecuteAppendAsync(
                sql, connection, effectiveTransaction, tenantId,
                eventsList, dcbQuery, expectedPosition,
                useAllTagsFunction, cancellationToken);

            if (ownedTransaction is not null)
                await ownedTransaction.CommitAsync(cancellationToken);

            return results;
        }
        finally
        {
            if (ownedTransaction is not null)
                await ownedTransaction.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------------------
    // Append command execution
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Executes an append command and reads back the resulting event envelopes.
    /// When <paramref name="tenantId"/> is non-null, <c>@p_tenant_id</c> is added as
    /// the first parameter and forwarded to <see cref="ReadEvent"/> so the returned
    /// envelopes carry the correct tenant (the append result set never includes a
    /// <c>tenant_id</c> column in either path).
    /// </summary>
    private static async Task<IReadOnlyCollection<IEventEnvelope>> ExecuteAppendAsync(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string? tenantId,
        List<IEventToPersist> eventsList,
        DcbQuery? dcbQuery,
        long? expectedPosition,
        bool useAllTagsFunction,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);

        // BuildEventsJson uses Utf8JsonWriter.WriteRawValue so the caller-supplied
        // event_data JSON is written verbatim — no JsonDocument.Parse / ArrayPool leak.
        var eventsJson = BuildEventsJson(eventsList);

        if (tenantId is not null)
            cmd.Parameters.AddWithValue("p_tenant_id", tenantId);

        cmd.Parameters.Add(new NpgsqlParameter("p_events", NpgsqlDbType.Jsonb) { Value = eventsJson });

        if (dcbQuery != null && expectedPosition.HasValue)
        {
            cmd.Parameters.AddWithValue("p_dcb_types",
                dcbQuery.Types.Count > 0 ? dcbQuery.Types.Select(t => t.Id).ToArray() : DBNull.Value);

            if (useAllTagsFunction)
            {
                cmd.Parameters.AddWithValue("p_dcb_all_tags",
                    dcbQuery.Tags.Count > 0 ? dcbQuery.Tags.Select(t => t.Value).ToArray() : DBNull.Value);
            }
            else
            {
                cmd.Parameters.AddWithValue("p_dcb_tags",
                    dcbQuery.Tags.Count > 0 ? dcbQuery.Tags.Select(t => t.Value).ToArray() : DBNull.Value);
            }

            cmd.Parameters.AddWithValue("p_expected_position", expectedPosition.Value);
        }
        else
        {
            cmd.Parameters.AddWithValue("p_dcb_types", DBNull.Value);

            if (useAllTagsFunction)
                cmd.Parameters.AddWithValue("p_dcb_all_tags", DBNull.Value);
            else
                cmd.Parameters.AddWithValue("p_dcb_tags", DBNull.Value);

            cmd.Parameters.AddWithValue("p_expected_position", DBNull.Value);
        }

        try
        {
            var results = new List<IEventEnvelope>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            // Resolve ordinals once per reader — not per row.
            // The append result set does not include a tenant_id column in either path;
            // the tenant is forwarded via the tenantId override in ReadEvent.
            var ord = new EventColumnOrdinals(reader, includeTenantId: false);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadEvent(reader, in ord, tenantId));

            return results;
        }
        catch (PostgresException ex) when (ex.SqlState == "P0001" && ex.Message.Contains("DCB conflict"))
        {
            throw new DcbConflictException(
                "DCB conflict detected: events matching the query exist after the expected position", ex);
        }
    }

    // ---------------------------------------------------------------------------
    // Shared position and stable-head queries
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Returns the global positions of all events in the window
    /// <c>(afterPosition, afterPosition + windowSize]</c>.
    /// This query targets the raw <c>alberto_events</c> table and is not tenant-scoped;
    /// the same implementation serves both the single-tenant and multi-tenant paths.
    /// </summary>
    internal static async Task<IReadOnlyList<long>> GetPositionsAsync(
        SchemaQualifier schema, NpgsqlDataSource dataSource,
        long afterPosition, int windowSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT global_position FROM {schema.Table("alberto_events")} " +
            "WHERE global_position > @after AND global_position <= @ceiling ORDER BY global_position",
            connection);
        cmd.Parameters.AddWithValue("after", afterPosition);
        cmd.Parameters.AddWithValue("ceiling", afterPosition + windowSize);
        var positions = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            positions.Add(reader.GetInt64(0));
        return positions;
    }

    /// <summary>
    /// Returns the highest global position that is fully stable (every inserting
    /// transaction has committed and predates every in-flight transaction).
    /// Returns <see cref="long.MaxValue"/> when the barrier is disabled or when all
    /// visible events are stable.
    /// <para>
    /// The head may safely advance through the contiguous prefix of events whose
    /// inserting transaction has committed and is older than every currently
    /// in-flight transaction (<c>pg_snapshot_xmin</c>). We find the FIRST position
    /// after <paramref name="afterPosition"/> that is NOT yet stable (committed but
    /// younger than the oldest running transaction) and clamp the head just below it.
    /// Because the append path assigns <c>global_position</c> and the xid under the
    /// same advisory lock, a committed event above an uncommitted one is itself
    /// non-stable, so this boundary never advances past an in-flight append. Rows with
    /// <c>NULL pg_xact_id</c> predate migration 008 and are always stable. Ascending
    /// index scan from the head — stops at the first non-stable row (the recent tail),
    /// so it stays cheap even when a long-running transaction pins xmin.
    /// </para>
    /// <para>
    /// The <c>::TEXT::BIGINT</c> double-cast is REQUIRED and must not be "simplified"
    /// to <c>::BIGINT</c>: <c>pg_snapshot_xmin</c> returns <c>xid8</c>, and PostgreSQL
    /// has no direct <c>xid8→bigint</c> cast (fails at runtime with "cannot cast type
    /// xid8 to bigint"). It is a single per-query STABLE scalar used as an index scan
    /// bound, so the text round-trip is evaluated once per call, not per row —
    /// negligible cost.
    /// </para>
    /// <para>
    /// This implementation is shared by both the single-tenant and multi-tenant paths;
    /// the subscription axis is always global-position-ordered, never tenant-scoped.
    /// </para>
    /// </summary>
    internal static async Task<long> GetStableHeadAsync(
        SchemaQualifier schema, NpgsqlDataSource dataSource,
        bool enabled, long afterPosition, CancellationToken cancellationToken)
    {
        if (!enabled)
            return long.MaxValue;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT global_position FROM {schema.Table("alberto_events")} " +
            "WHERE global_position > @after " +
            "AND pg_xact_id IS NOT NULL " +
            "AND pg_xact_id >= pg_snapshot_xmin(pg_current_snapshot())::TEXT::BIGINT " +
            "ORDER BY global_position ASC LIMIT 1",
            connection);
        cmd.Parameters.AddWithValue("after", afterPosition);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long firstNonStable ? firstNonStable - 1 : long.MaxValue;
    }

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
        // TIMESTAMPTZ maps natively to DateTimeOffset in Npgsql — no SpecifyKind fix-up needed.
        var createdAt = reader.GetFieldValue<DateTimeOffset>(ord.CreatedAt);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(eventMetadata) ?? [];

        // Parse the schema version from the reserved _version:N tag via the shared helper.
        // Events written before versioning was introduced carry no such tag and default to v1.
        var schemaVersion = EventVersionTag.ParseFromRawTags(eventTags);

        return new EventEnvelope
        {
            Id = eventId,
            TenantId = effectiveTenantId,
            GlobalPosition = globalPosition,
            EventType = new EventType(eventType, schemaVersion),
            // EventTag.FromStorage skips the validation regex — tags stored in the
            // DB are already valid by construction (validated at write time).
            Tags = eventTags.Select(EventTag.FromStorage).ToArray(),
            EventData = eventData,
            Metadata = metadata,
            CreatedAt = createdAt
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
