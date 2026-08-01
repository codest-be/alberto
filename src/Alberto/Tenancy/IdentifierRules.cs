using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Alberto.Tenancy;

/// <summary>
/// The shared identifier rules used for module keys, shard ids, and tenant ids.
/// A separate, looser rule covers processor ids — see <see cref="IsValidProcessorId"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every string that ends up composed into a DI service key, a metric tag, or a lease-holder
/// name must satisfy at least one of the rules here. The DI key for a projection's reader store
/// factory is <c>{moduleKey}:{processorId}</c>; the key for a sharded module backend is
/// <c>{moduleKey}#{shardId}</c>. A <c>:</c> or <c>#</c> in the wrong position makes those keys
/// unparseable or ambiguous, causing silent wrong-service resolution at runtime.
/// </para>
/// <para>
/// All three strict-identifier types (module keys, shard ids, tenant ids) share the same
/// <c>^[a-z][a-z0-9_]{0,62}$</c> regex. Keeping them identical means a string that is safe in
/// one context is safe in every composed key without per-site quoting. The 63-character ceiling
/// matches PostgreSQL's identifier limit, so an Alberto identifier can always be used verbatim
/// as a schema name.
/// </para>
/// </remarks>
internal static partial class IdentifierRules
{
    /// <summary>
    /// The human-readable statement of the strict identifier rule, for inclusion in error messages.
    /// Applies to module keys, shard ids, and tenant ids.
    /// </summary>
    public const string Rule =
        "Identifiers must start with a lowercase letter and contain only lowercase letters, " +
        "digits, and underscores, with a maximum length of 63 characters.";

    /// <summary>
    /// The human-readable statement of the processor id rule, for inclusion in error messages.
    /// </summary>
    /// <remarks>
    /// Processor ids are intentionally more permissive than module keys: they are often derived
    /// from handler type names (<c>OrderSummaryProjection</c>) or attribute values
    /// (<c>orders.summary</c>), neither of which is lowercase-only. The only characters that
    /// create structural ambiguity in the composed DI key <c>{moduleKey}:{processorId}</c> are
    /// <c>:</c> and <c>#</c>; the only string values that collide with Alberto's own internal DI
    /// registrations are the reserved words listed in <see cref="ReservedProcessorIds"/>.
    /// </remarks>
    public const string ProcessorIdRule =
        "Processor ids must not contain ':' or '#', and must not be a reserved word " +
        "(\"consumer\", \"catalog\", \"tenant-raw\"). These characters and words are used " +
        "internally by Alberto to compose DI service keys.";

    /// <summary>
    /// Processor id suffixes that Alberto composes internally as DI service keys
    /// <c>{moduleKey}:{suffix}</c>. A processor whose id is one of these would shadow the
    /// internal registration, causing the wrong service to be resolved at runtime.
    /// </summary>
    /// <remarks>
    /// "tenant-raw" is already blocked by the format check (<see cref="IsValidProcessorId"/>
    /// rejects the hyphen), but listing it here makes the reservation self-documenting and
    /// future-proofs against a relaxed format rule.
    /// </remarks>
    public static readonly IReadOnlySet<string> ReservedProcessorIds =
        new HashSet<string>(StringComparer.Ordinal) { "consumer", "catalog", "tenant-raw" };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> matches
    /// <c>^[a-z][a-z0-9_]{0,62}$</c>: a lowercase letter followed by up to 62 lowercase
    /// letters, digits, or underscores.
    /// </summary>
    /// <remarks>
    /// Deliberately the same allowlist Alberto applies to schema names, tenant ids, and shard ids,
    /// so an identifier that passes this check can be used verbatim in any of those contexts
    /// without quoting.
    /// </remarks>
    public static bool IsValidIdentifier([NotNullWhen(true)] string? value) =>
        value is not null && IdentifierPattern().IsMatch(value);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="processorId"/> is safe to use as a
    /// processor id inside a composed DI service key <c>{moduleKey}:{processorId}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Processor ids do not have to be lowercase. They are often derived from handler type names
    /// (e.g., <c>OrderSummaryProjection</c>) or pinned with the <c>[ProcessorId]</c> attribute
    /// using dotted names (e.g., <c>orders.summary</c>). Neither needs to satisfy the stricter
    /// module-key rule.
    /// </para>
    /// <para>
    /// What does matter is structural integrity of the composed key. A <c>:</c> creates a second
    /// separator in the composed key, making the processor id ambiguous with a nested suffix. A
    /// <c>#</c> creates a separator that <see cref="ShardKey.TryParse"/> parses, making the key
    /// look like a shard key for the wrong module. A reserved word collides with an Alberto
    /// internal registration: when the processor id is "catalog", the composed key equals
    /// <c>{moduleKey}:catalog</c>, which is already the DI key for the Postgres catalog data source.
    /// </para>
    /// </remarks>
    public static bool IsValidProcessorId([NotNullWhen(true)] string? processorId)
    {
        if (string.IsNullOrWhiteSpace(processorId))
            return false;

        // ':' is the separator between module key and processor id in the reader store factory key.
        // '#' is the separator between module key and shard id in the shard backend key.
        // Both are structural: a processorId that contains either one makes the composed key
        // ambiguous or breaks the parser that splits it.
        if (processorId.Contains(':', StringComparison.Ordinal)
            || processorId.Contains('#', StringComparison.Ordinal))
            return false;

        // Reserved words collide with Alberto's own internal DI key suffixes. Allowing a processor
        // id of "catalog" would shadow the {moduleKey}:catalog data source registration; "consumer"
        // would shadow the {moduleKey}:consumer backend registration.
        return !ReservedProcessorIds.Contains(processorId);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,62}$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex IdentifierPattern();
}
