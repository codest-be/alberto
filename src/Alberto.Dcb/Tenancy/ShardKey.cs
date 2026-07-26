using System.Diagnostics.CodeAnalysis;

namespace Alberto.Dcb.Tenancy;

/// <summary>
/// Composes and parses the physical DI key of a sharded module, <c>module#shard</c>.
/// </summary>
/// <remarks>
/// A sharded module registers its services once per shard, and each copy needs a key of its own.
/// The composed key is what every existing registration sees as its module key, so nothing else
/// in the codebase has to know that sharding exists. Only this class assembles or takes apart
/// that string; anywhere else it is opaque.
/// </remarks>
public static class ShardKey
{
    /// <summary>Separates the logical module key from the shard id.</summary>
    public const char Separator = '#';

    /// <summary>Returns the physical DI key for <paramref name="shardId"/> of <paramref name="moduleKey"/>.</summary>
    public static string Compose(string moduleKey, string shardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        return $"{moduleKey}{Separator}{shardId}";
    }

    /// <summary>
    /// Splits a composed key back into its parts. Returns false for a key that was never
    /// composed — an unsharded module key — and for anything malformed.
    /// </summary>
    public static bool TryParse(
        string? key,
        [NotNullWhen(true)] out string? moduleKey,
        [NotNullWhen(true)] out string? shardId)
    {
        moduleKey = null;
        shardId = null;

        if (string.IsNullOrEmpty(key))
            return false;

        // IndexOf rather than Split: this runs per event on the telemetry path, where the
        // overwhelmingly common answer is "not a shard key" and should allocate nothing at all.
        var separator = key.IndexOf(Separator);
        if (separator <= 0 || separator == key.Length - 1)
            return false;

        // A second separator means the key was never composed by Compose.
        if (key.IndexOf(Separator, separator + 1) >= 0)
            return false;

        moduleKey = key[..separator];
        shardId = key[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// Returns the logical module key of <paramref name="key"/>: the part before the separator
    /// when it is a shard key, and the key itself otherwise.
    /// </summary>
    public static string ModuleOf(string key) =>
        TryParse(key, out var moduleKey, out _) ? moduleKey : key;

    /// <summary>
    /// Returns the shard id of <paramref name="key"/>, or null when it is not a shard key.
    /// </summary>
    public static string? ShardOf(string key) =>
        TryParse(key, out _, out var shardId) ? shardId : null;

    /// <summary>
    /// Whether <paramref name="shardId"/> is safe to use in a DI key, a metric tag and a
    /// lease-holder name. Delegates to <see cref="IdentifierRules.IsValidIdentifier"/>, which
    /// is the single shared implementation of the allowlist that Alberto applies to module keys,
    /// shard ids, and tenant ids.
    /// </summary>
    public static bool IsValidShardId([NotNullWhen(true)] string? shardId) =>
        IdentifierRules.IsValidIdentifier(shardId);
}
