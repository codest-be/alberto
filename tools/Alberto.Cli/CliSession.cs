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
    private readonly CliConfigurationException? _configurationError;

    /// <summary>The output adapter in effect for this invocation.</summary>
    public IOutput Output { get; }

    /// <summary>
    /// Who to record as having performed a mutation. Every destructive command routes through
    /// <see cref="Alberto.Admin.IAdminOperator"/>, which appends an audit event carrying this
    /// value in the same transaction as the change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the operating-system account the CLI is running as. It is an attribution, not an
    /// authentication: the CLI talks straight to PostgreSQL with whatever connection string it was
    /// given, and anyone who can run it can set <c>USER</c> to anything. What the audit trail
    /// answers is "which account did this, and when" for a team that is not trying to deceive each
    /// other — it does not answer "prove it" for one that is. Treat database credentials as the
    /// actual access control.
    /// </para>
    /// <para>
    /// Empty in the odd environment that exposes no user name (some containers), in which case
    /// <c>"unknown"</c> is recorded rather than an empty string, so the audit event never carries a
    /// blank that reads as a bug.
    /// </para>
    /// </remarks>
    public static string OperatorId =>
        string.IsNullOrWhiteSpace(Environment.UserName) ? "unknown" : Environment.UserName;

    /// <param name="json">When <see langword="true"/>, selects <see cref="JsonOutput"/>.</param>
    /// <param name="config">
    /// Config already in hand — used by tests and callers that already resolved it. When
    /// <see langword="null"/>, the config is loaded once from disk via
    /// <see cref="ConfigFileFinder.Find()"/>.
    /// </param>
    /// <param name="output">
    /// The adapter to write through, overriding the one <paramref name="json"/> would select. Both
    /// shipped adapters write to <see cref="Console.Error"/>, which is process-global, so a test
    /// that asserts on what an operator was told has to redirect the whole process to read it back
    /// — and then it cannot run beside anything else that writes there. Passing a double instead
    /// keeps that assertion scoped to the one session under test.
    /// </param>
    public CliSession(bool json, AlbertoConfig? config = null, IOutput? output = null)
    {
        Output = output ?? (json ? new JsonOutput() : new HumanOutput());

        if (config is not null)
        {
            _config = config;
            return;
        }

        try
        {
            _config = ConfigFileFinder.Find() ?? new AlbertoConfig();
        }
        catch (CliConfigurationException ex)
        {
            _configurationError = ex;
            _config = new AlbertoConfig();
        }
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
        if (_configurationError is not null)
        {
            Output.Error(_configurationError.Message);
            return 1;
        }

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
