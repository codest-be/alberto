namespace Alberto.Cli;

/// <summary>
/// One database a command should run against.
/// </summary>
/// <param name="ShardId">
/// The shard's id, or null when the module has one database — which is what makes the
/// unsharded output identical to what it was before shards existed.
/// </param>
public sealed record ShardTarget(string? ShardId, string ConnectionString, string Schema);

/// <summary>
/// Raised when a command cannot tell which databases the operator meant. Carries a message that
/// names them, so the operator can retype the command with a selection rather than guess.
/// </summary>
public sealed class ShardSelectionException(string message) : Exception(message);

/// <summary>
/// Turns <c>--shard</c>/<c>--all-shards</c> and <c>.alberto/config.json</c> into the list of
/// databases a command runs against.
/// </summary>
/// <remarks>
/// A config with no <c>shards</c> always yields exactly one target with a null
/// <see cref="ShardTarget.ShardId"/>, whatever the flags say, so every command behaves as it did
/// before sharding for the modules that do not use it.
/// <para>
/// Reads and mutations differ deliberately. A read with no selection fans out — seeing every
/// database is what an operator wants from <c>checkpoints</c> or <c>dead-letters</c>. A mutation
/// with no selection refuses: rewinding a checkpoint across every tenant's database because the
/// flag was forgotten is not something an operator can undo.
/// </para>
/// </remarks>
public static class ShardResolver
{
    /// <summary>
    /// The databases a read should cover: the one named by <paramref name="shardOption"/>, or all
    /// of them. Reads take no <c>--all-shards</c> because that is already what they do.
    /// </summary>
    /// <param name="shardOption">The <c>--shard</c> value, or null.</param>
    /// <param name="urlOption">The <c>--url</c> value, or null.</param>
    /// <param name="schemaOption">The <c>--schema</c> value, or null.</param>
    public static IReadOnlyList<ShardTarget> ResolveForRead(
        string? shardOption, string? urlOption, string? schemaOption) =>
        ResolveForRead(ConfigFileFinder.Find(), shardOption, urlOption, schemaOption);

    /// <inheritdoc cref="ResolveForRead(string?, string?, string?)" />
    /// <param name="config">The config to decide from, rather than the one on disk.</param>
    public static IReadOnlyList<ShardTarget> ResolveForRead(
        AlbertoConfig? config, string? shardOption, string? urlOption, string? schemaOption)
    {
        var shards = Shards(config, urlOption, schemaOption);
        if (shards is null)
            return [Unsharded(config, urlOption, schemaOption)];

        return shardOption is null ? shards : [Named(shards, shardOption)];
    }

    /// <summary>
    /// The databases a mutation should cover, or a <see cref="ShardSelectionException"/> when the
    /// operator named none and the module has more than one.
    /// </summary>
    /// <param name="supportsAllShards">
    /// False for a command that only ever changes one database — <c>checkpoint set</c>, whose
    /// position number means something different in each of them. It changes only the advice in
    /// the message, since such a command never passes <paramref name="allShards"/> as true.
    /// </param>
    public static IReadOnlyList<ShardTarget> ResolveForMutation(
        string? shardOption, bool allShards, string? urlOption, string? schemaOption,
        bool supportsAllShards = true) =>
        ResolveForMutation(
            ConfigFileFinder.Find(), shardOption, allShards, urlOption, schemaOption, supportsAllShards);

    /// <inheritdoc cref="ResolveForMutation(string?, bool, string?, string?, bool)" />
    /// <param name="config">The config to decide from, rather than the one on disk.</param>
    public static IReadOnlyList<ShardTarget> ResolveForMutation(
        AlbertoConfig? config, string? shardOption, bool allShards, string? urlOption, string? schemaOption,
        bool supportsAllShards = true)
    {
        var shards = Shards(config, urlOption, schemaOption);
        if (shards is null)
            return [Unsharded(config, urlOption, schemaOption)];

        if (shardOption is not null)
            return [Named(shards, shardOption)];

        if (allShards)
            return shards;

        throw new ShardSelectionException(
            $"This module is spread over {shards.Count} databases " +
            $"({string.Join(", ", shards.Select(s => s.ShardId))}). " +
            (supportsAllShards
                ? "Pass --shard <id> to change one of them, or --all-shards to change every one."
                : "Pass --shard <id> to name the one you mean. This command takes no --all-shards: " +
                  "a position is a per-database sequence, so one number cannot apply to several."));
    }

    /// <summary>The control database holding the tenant → shard catalog.</summary>
    /// <exception cref="ShardSelectionException">When the config declares no catalog.</exception>
    public static ShardTarget ResolveCatalog(string? urlOption, string? schemaOption) =>
        ResolveCatalog(ConfigFileFinder.Find(), urlOption, schemaOption);

    /// <inheritdoc cref="ResolveCatalog(string?, string?)" />
    /// <param name="config">The config to decide from, rather than the one on disk.</param>
    public static ShardTarget ResolveCatalog(AlbertoConfig? config, string? urlOption, string? schemaOption)
    {
        // An explicit --url is the operator pointing at a database directly, which is the escape
        // hatch for a catalog that is not in the config file at all.
        if (!string.IsNullOrWhiteSpace(urlOption))
            return new ShardTarget(null, urlOption, ConnectionResolver.ResolveSchema(config, schemaOption));

        if (string.IsNullOrWhiteSpace(config?.Catalog?.Url))
        {
            throw new ShardSelectionException(
                "No tenant shard catalog is configured. Add a \"catalog\": { \"url\": \"...\" } " +
                "entry to .alberto/config.json, or pass --url pointing at the control database.");
        }

        return new ShardTarget(
            null,
            config.Catalog.Url,
            SchemaOf(schemaOption, config.Catalog.Schema, config.Schema));
    }

    /// <summary>
    /// The shard ids declared in <c>.alberto/config.json</c>, in id order. Empty for a
    /// single-database module.
    /// </summary>
    /// <remarks>
    /// The catalog stores ids and nothing else, so this is the only place that knows whether an
    /// id an operator typed — or one already in the catalog — resolves to a database at all.
    /// </remarks>
    public static IReadOnlyList<string> DeclaredShardIds() => DeclaredShardIds(ConfigFileFinder.Find());

    /// <inheritdoc cref="DeclaredShardIds()" />
    /// <param name="config">The config to read, rather than the one on disk.</param>
    public static IReadOnlyList<string> DeclaredShardIds(AlbertoConfig? config)
    {
        var shards = ConfiguredShards(config);
        if (shards is null)
            return [];

        return shards.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The module key the catalog's rows are filed under. Part of the catalog's primary key, so a
    /// wrong one silently reads an empty table rather than failing.
    /// </summary>
    public static string ResolveModuleKey(string? moduleOption) =>
        ResolveModuleKey(ConfigFileFinder.Find(), moduleOption);

    /// <inheritdoc cref="ResolveModuleKey(string?)" />
    /// <param name="config">The config to read, rather than the one on disk.</param>
    public static string ResolveModuleKey(AlbertoConfig? config, string? moduleOption)
    {
        if (!string.IsNullOrWhiteSpace(moduleOption))
            return moduleOption;

        if (!string.IsNullOrWhiteSpace(config?.Module))
            return config.Module;

        throw new ShardSelectionException(
            "No module key is configured. Add a \"module\": \"orders\" entry to " +
            ".alberto/config.json, or pass --module. It must match the key the application " +
            "passes to AddAlberto, because the catalog is keyed on it.");
    }

    /// <summary>
    /// The declared shards, or null when this is a single-database module. An explicit
    /// <c>--url</c> also returns null: the operator has named a database, and fanning out over
    /// the config's shards while ignoring it would be a surprise.
    /// </summary>
    private static IReadOnlyList<ShardTarget>? Shards(
        AlbertoConfig? config, string? urlOption, string? schemaOption)
    {
        if (!string.IsNullOrWhiteSpace(urlOption))
            return null;

        var shards = ConfiguredShards(config);
        if (shards is null)
            return null;

        return shards
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new ShardTarget(
                s.Key,
                s.Value.Url!,
                SchemaOf(schemaOption, s.Value.Schema, config!.Schema)))
            .ToArray();
    }

    /// <summary>
    /// Returns the complete shard map. A partially configured map is rejected as a unit: silently
    /// omitting one entry would make an all-shards mutation report success after touching only a
    /// subset of the module.
    /// </summary>
    private static IReadOnlyDictionary<string, ShardConfig>? ConfiguredShards(AlbertoConfig? config)
    {
        if (config?.Shards is not { Count: > 0 } shards)
            return null;

        var incomplete = shards
            .Where(s => string.IsNullOrWhiteSpace(s.Value?.Url))
            .Select(s => s.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (incomplete.Length > 0)
        {
            throw new ShardSelectionException(
                $"Shard configuration is incomplete. Add a non-empty \"url\" for: " +
                $"{string.Join(", ", incomplete)}. No shard was selected.");
        }

        return shards;
    }

    private static ShardTarget Unsharded(AlbertoConfig? config, string? urlOption, string? schemaOption) => new(
        null,
        ConnectionResolver.ResolveConnectionString(config, urlOption),
        ConnectionResolver.ResolveSchema(config, schemaOption));

    private static ShardTarget Named(IReadOnlyList<ShardTarget> shards, string shardId)
    {
        var match = shards.FirstOrDefault(s => string.Equals(s.ShardId, shardId, StringComparison.Ordinal));
        if (match is null)
        {
            throw new ShardSelectionException(
                $"No shard '{shardId}' is configured. Declared shards: " +
                $"{string.Join(", ", shards.Select(s => s.ShardId))}.");
        }

        return match;
    }

    /// <summary>Command line beats the shard's own schema, which beats the module's.</summary>
    private static string SchemaOf(string? fromCommandLine, string? fromShard, string? fromModule)
    {
        if (!string.IsNullOrWhiteSpace(fromCommandLine))
            return fromCommandLine;
        if (!string.IsNullOrWhiteSpace(fromShard))
            return fromShard;
        if (!string.IsNullOrWhiteSpace(fromModule))
            return fromModule;
        return "public";
    }
}
