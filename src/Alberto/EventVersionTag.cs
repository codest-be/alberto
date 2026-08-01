namespace Alberto;

/// <summary>
/// Shared helper for extracting the schema version from persisted event tags.
/// Both the in-memory and PostgreSQL backends derive <see cref="EventType.Version"/>
/// from the stored <c>_version:N</c> tag via this single implementation, so neither backend
/// can drift to trusting the caller's input object instead of what is in storage.
/// </summary>
/// <remarks>
/// Storage is the source of truth on read. An event appended with
/// <c>EventType.Version = 5</c> but carrying <c>_version:3</c> in its tags must be read back
/// as version 3 — the tag is what was persisted, and the upcaster chain is driven by
/// that value, not by whatever the caller's in-memory object said at write time.
/// </remarks>
internal static class EventVersionTag
{
    /// <summary>
    /// Extracts the schema version from a collection of <see cref="EventTag"/> objects
    /// by scanning for the reserved <c>_version:N</c> concept.
    /// Returns <c>1</c> when no such tag is present (pre-versioning events or direct
    /// callers that omit the tag).
    /// </summary>
    /// <remarks>
    /// Used by <c>InMemoryEventStoreBackend</c> on its read path, where tags are already
    /// parsed into <see cref="EventTag"/> structs.
    /// </remarks>
    internal static int ParseFromTags(IReadOnlyCollection<EventTag> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Concept == EventTag.SchemaVersionConcept)
            {
                if (int.TryParse(tag.Id, out var v) && v >= 1)
                    return v;
                // Malformed _version tag — treat as absent, fall through to default.
                break;
            }
        }

        return 1;
    }

    /// <summary>
    /// Extracts the schema version from a raw string tag array by scanning for a tag
    /// that starts with the <c>_version:</c> prefix.
    /// Returns <c>1</c> when no such tag is present (pre-versioning events).
    /// </summary>
    /// <remarks>
    /// Used by <c>PostgresBackendHelpers.ReadEvent</c> on its read path, where tags
    /// arrive as raw strings from the Npgsql array column.
    /// </remarks>
    internal static int ParseFromRawTags(string[] rawTags)
    {
        var schemaPrefix = EventTag.SchemaVersionConcept + ":";
        foreach (var rawTag in rawTags)
        {
            if (rawTag.StartsWith(schemaPrefix, StringComparison.Ordinal))
            {
                if (int.TryParse(rawTag.AsSpan(schemaPrefix.Length), out var v) && v >= 1)
                    return v;
                // Malformed _version tag — treat as absent, fall through to default.
                break;
            }
        }

        return 1;
    }
}
