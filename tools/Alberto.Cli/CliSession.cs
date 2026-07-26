using Alberto.Cli.Output;
using Spectre.Console;

namespace Alberto.Cli;

/// <summary>
/// The context for one CLI invocation — owns every cross-cutting concern so command handlers
/// can shrink to: declare options, name an operation, hand it here.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Config resolution once.</b> <see cref="AlbertoConfig"/> is read from disk at most
///     once per invocation and passed down through every resolver, eliminating repeated
///     directory-tree walks.</item>
///   <item><b>Output adapter selection.</b> JSON vs human-readable decided once from
///     <c>--json</c>.</item>
///   <item><b>Shard target resolution.</b> Read-only and destructive fan-out driven from the
///     pre-loaded config.</item>
///   <item><b>Confirmation policy.</b> Destructive operations gate on <c>--yes</c> or an
///     interactive prompt; non-interactive contexts get a clear error instead of a hang.</item>
///   <item><b>Error handling and exit codes.</b> <see cref="RunAsync"/> catches exceptions and
///     maps them to exit code 1, eliminating scattered <c>Environment.Exit</c> calls from
///     handlers.</item>
/// </list>
/// </remarks>
public sealed class CliSession
{
    private readonly AlbertoConfig _config;

    /// <summary>The output adapter in effect for this invocation.</summary>
    public IOutput Output { get; }

    /// <param name="json">When <see langword="true"/>, selects <see cref="JsonOutput"/>.</param>
    /// <param name="config">
    /// Config already in hand — used by tests and callers that already resolved it. When
    /// <see langword="null"/>, the config is loaded once from disk via
    /// <see cref="ConfigFileFinder.Find()"/>.
    /// </param>
    public CliSession(bool json, AlbertoConfig? config = null)
    {
        Output = json ? new JsonOutput() : new HumanOutput();
        _config = config ?? ConfigFileFinder.Find() ?? new AlbertoConfig();
    }

    /// <summary>
    /// Resolves the shard targets for a read-only command. Uses the pre-loaded config so the
    /// directory tree is not walked again.
    /// </summary>
    public IReadOnlyList<ShardTarget> ReadTargets(string? shard, string? url, string? schema) =>
        ShardResolver.ResolveForRead(_config, shard, url, schema);

    /// <summary>
    /// Resolves the shard targets for a destructive command. Refuses without an explicit
    /// selection on a sharded module.
    /// </summary>
    public IReadOnlyList<ShardTarget> MutationTargets(
        string? shard, bool allShards, string? url, string? schema,
        bool supportsAllShards = true) =>
        ShardResolver.ResolveForMutation(_config, shard, allShards, url, schema, supportsAllShards);

    /// <summary>
    /// The control database holding the tenant → shard catalog. Uses the pre-loaded config.
    /// </summary>
    /// <exception cref="ShardSelectionException">When the config declares no catalog.</exception>
    public ShardTarget CatalogTarget(string? url, string? schema) =>
        ShardResolver.ResolveCatalog(_config, url, schema);

    /// <summary>
    /// The module key the catalog's rows are filed under. Uses the pre-loaded config.
    /// </summary>
    /// <exception cref="ShardSelectionException">When no module key is configured or given.</exception>
    public string ModuleKey(string? module) =>
        ShardResolver.ResolveModuleKey(_config, module);

    /// <summary>
    /// The shard ids declared in configuration, in id order. Uses the pre-loaded config.
    /// </summary>
    /// <remarks>
    /// This and <see cref="CatalogTarget"/> answer questions about the same file, so they have to
    /// answer them from the same read: a command that reports a catalog row as pointing at an
    /// undeclared shard is comparing the two, and comparing them across two reads of a file that
    /// an operator may be editing turns an edit into a bogus warning.
    /// </remarks>
    public IReadOnlyList<string> DeclaredShardIds() =>
        ShardResolver.DeclaredShardIds(_config);

    /// <summary>
    /// Gates a destructive operation on an explicit <c>--yes</c> flag or an interactive prompt.
    /// </summary>
    /// <param name="yes">Value of the <c>--yes</c> option.</param>
    /// <param name="prompt">The Spectre.Console prompt to show when interactive.</param>
    /// <param name="nonInteractiveError">
    /// The full error message written when the session is not interactive and
    /// <paramref name="yes"/> was not given. Callers supply the complete message (including the
    /// "how to skip" hint) so the exact wording per command is preserved.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the operation should proceed.
    /// <c>0</c> when the user interactively declined — a user-initiated abort is not an error
    /// and the caller should return exit code 0.
    /// <c>1</c> when the session is not interactive and <paramref name="yes"/> was not given —
    /// the caller should return exit code 1.
    /// </returns>
    /// <remarks>
    /// <code>
    /// if (session.Confirm(yes, prompt, error) is { } code) return code;
    /// </code>
    /// </remarks>
    public int? Confirm(bool yes, string prompt, string nonInteractiveError)
    {
        if (yes)
            return null;

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            Output.Error(nonInteractiveError);
            return 1;
        }

        if (AnsiConsole.Confirm(prompt, defaultValue: false))
            return null;

        Output.Text("Aborted.");
        return 0;
    }

    /// <summary>
    /// Executes an operation, catching exceptions and mapping them to exit codes so handlers do
    /// not need their own try/catch blocks.
    /// </summary>
    /// <param name="body">The operation to run. Returns 0 on success, non-zero on failure.</param>
    /// <returns>
    /// The exit code returned by <paramref name="body"/>, or 1 when it threw an exception.
    /// </returns>
    /// <remarks>
    /// <see cref="ShardSelectionException"/> is formatted as a plain error rather than a stack
    /// trace — it represents a user error (bad option combination), not a fault.
    /// </remarks>
    public async Task<int> RunAsync(Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (ShardSelectionException ex)
        {
            Output.Error(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Output.Error(ex.Message);
            return 1;
        }
    }
}
