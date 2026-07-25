using System.CommandLine;
using Alberto.Cli.Output;
using Alberto.Dcb.Postgres;
using Npgsql;

namespace Alberto.Cli;

/// <summary>What one database returned, or why it could not be reached.</summary>
public sealed record ShardResult<T>(ShardTarget Target, T? Value, Exception? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Runs a command's body against every database it was pointed at, and renders the results as one
/// table with a <c>Shard</c> column when there is more than one.
/// </summary>
/// <remarks>
/// One shard being unreachable does not hide the others: the failure is reported and the rest of
/// the output still renders. The exit code is still non-zero, so a script cannot mistake a partial
/// answer for a complete one.
/// </remarks>
public static class ShardRun
{
    /// <summary>
    /// Adds <c>--shard</c> to a read command. There is no <c>--all-shards</c> here: covering every
    /// database is what a read does when no shard is named.
    /// </summary>
    public static Option<string?> AddReadOption(Command command)
    {
        var shard = new Option<string?>("--shard")
        {
            Description = "Read one shard only, by id. Ignored for single-database modules.",
        };

        command.AddOption(shard);
        return shard;
    }

    /// <summary>
    /// Adds <c>--shard</c> and <c>--all-shards</c> to a command that changes something. A mutation
    /// takes no default: see <see cref="ShardResolver.ResolveForMutation"/> for why.
    /// </summary>
    public static (Option<string?> Shard, Option<bool> AllShards) AddMutationOptions(Command command)
    {
        var shard = new Option<string?>("--shard")
        {
            Description = "The shard to change, by id. Ignored for single-database modules.",
        };
        var allShards = new Option<bool>("--all-shards")
        {
            Description = "Apply the change to every configured shard.",
        };

        command.AddOption(shard);
        command.AddOption(allShards);

        return (shard, allShards);
    }

    /// <summary>
    /// Opens each target in turn and runs <paramref name="body"/> against its admin data access.
    /// </summary>
    /// <remarks>
    /// Sequential rather than parallel: an operator command's cost is dominated by the operator
    /// reading its output, and a fan-out that opens every database at once is a worse neighbour on
    /// a cluster already under load.
    /// </remarks>
    public static Task<IReadOnlyList<ShardResult<T>>> CollectAsync<T>(
        IReadOnlyList<ShardTarget> targets,
        Func<PostgresAdminDataAccess, Task<T>> body) =>
        ProbeAsync(targets, (dataSource, target) =>
            body(new PostgresAdminDataAccess(dataSource, target.Schema)));

    /// <summary>
    /// The same fan-out as <see cref="CollectAsync"/> but handing the body the data source, for
    /// callers that need a store rather than the admin surface.
    /// </summary>
    /// <remarks>
    /// A destructive command uses this to look at every database before changing any of them, so
    /// that its one confirmation prompt can state what the whole run is about to do.
    /// </remarks>
    public static async Task<IReadOnlyList<ShardResult<T>>> ProbeAsync<T>(
        IReadOnlyList<ShardTarget> targets,
        Func<NpgsqlDataSource, ShardTarget, Task<T>> body)
    {
        var results = new List<ShardResult<T>>(targets.Count);

        foreach (var target in targets)
        {
            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(target.ConnectionString).Build();
                results.Add(new ShardResult<T>(target, await body(dataSource, target), null));
            }
            catch (Exception ex)
            {
                results.Add(new ShardResult<T>(target, default, ex));
            }
        }

        return results;
    }

    /// <summary>
    /// A phrase naming the databases a change is about to touch, for a confirmation prompt. Empty
    /// for a single-database module, so its prompt reads exactly as it did before sharding.
    /// </summary>
    public static string Scope(IReadOnlyList<ShardTarget> targets)
    {
        if (!ShowsShard(targets))
            return string.Empty;

        return targets.Count == 1
            ? $" in shard '{targets[0].ShardId}'"
            : $" in all {targets.Count} shards ({string.Join(", ", targets.Select(t => t.ShardId))})";
    }

    /// <summary>
    /// Runs a change against each target in turn, announcing which database it lands in and
    /// carrying on past a failure. Returns whether any target failed.
    /// </summary>
    /// <remarks>
    /// Carrying on matters more here than for a read: stopping at the first unreachable shard
    /// would leave a fleet half-changed with no record of which half, and the operator would have
    /// to work out where to resume. Every shard is attempted and every failure is named.
    /// </remarks>
    public static async Task<bool> ApplyAsync(
        IOutput output,
        IReadOnlyList<ShardTarget> targets,
        Func<NpgsqlDataSource, ShardTarget, Task> body)
    {
        var showShard = ShowsShard(targets);
        var failed = false;

        foreach (var target in targets)
        {
            // Suppressed in JSON mode, where each shard emits its own object instead.
            if (showShard)
                output.Text($"[{target.ShardId}]");

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(target.ConnectionString).Build();
                await body(dataSource, target);
            }
            catch (Exception ex)
            {
                output.Error(showShard ? $"shard '{target.ShardId}': {ex.Message}" : ex.Message);
                failed = true;
            }
        }

        return failed;
    }

    /// <summary>
    /// Reports every failure in <paramref name="results"/> and returns whether any occurred. A
    /// caller that gets true should exit non-zero once it has rendered what did succeed.
    /// </summary>
    public static bool ReportFailures<T>(IOutput output, IReadOnlyList<ShardResult<T>> results)
    {
        var failed = results.Where(r => !r.Succeeded).ToArray();

        foreach (var result in failed)
        {
            output.Error(result.Target.ShardId is null
                ? result.Failure!.Message
                : $"shard '{result.Target.ShardId}': {result.Failure!.Message}");
        }

        return failed.Length > 0;
    }

    /// <summary>
    /// Whether output should carry a shard column. False for a single-database module, so its
    /// tables and JSON are byte-for-byte what they were before sharding existed.
    /// </summary>
    public static bool ShowsShard(IReadOnlyList<ShardTarget> targets) =>
        targets.Any(t => t.ShardId is not null);

    /// <summary>
    /// Renders one table across every shard's rows, prefixing a <c>Shard</c> column when there is
    /// more than one database in play.
    /// </summary>
    public static void Table<T>(
        IOutput output,
        IReadOnlyList<ShardTarget> targets,
        IReadOnlyList<ShardResult<IReadOnlyList<T>>> results,
        string[] headers,
        Func<T, string[]> row,
        string emptyMessage)
    {
        var showShard = ShowsShard(targets);

        var rows = results
            .Where(r => r.Succeeded)
            .SelectMany(r => r.Value!.Select(item => showShard
                ? [r.Target.ShardId!, .. row(item)]
                : row(item)))
            .ToArray();

        if (rows.Length == 0)
        {
            output.Text(emptyMessage);
            return;
        }

        output.Table(showShard ? ["Shard", .. headers] : headers, rows);
    }

    /// <summary>
    /// Flattens every shard's rows into objects for JSON output, tagging each with its shard when
    /// there is more than one database in play.
    /// </summary>
    public static IEnumerable<object> Flatten<T>(
        IReadOnlyList<ShardTarget> targets,
        IReadOnlyList<ShardResult<IReadOnlyList<T>>> results,
        Func<T, object> project)
    {
        var showShard = ShowsShard(targets);

        return results
            .Where(r => r.Succeeded)
            .SelectMany(r => r.Value!.Select(item => showShard
                ? new { shard = r.Target.ShardId, value = project(item) }
                : (object)project(item)));
    }
}
