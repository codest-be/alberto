using System.CommandLine;
using Alberto.Cli.Output;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Npgsql;
using Spectre.Console;

namespace Alberto.Cli.Commands.Ops;

/// <summary>
/// Drives the projection rebuild state machine from the operator side.
/// </summary>
/// <remarks>
/// This command only moves the state machine. The replay itself is carried out by the
/// running application, which needs <c>WithRebuilds()</c> on its control loop to have a
/// coordinator watching for the transitions this command makes.
/// <para>
/// A sharded module runs the same projection in every database, each with its own version
/// number and its own rebuild in flight. Every verb here therefore acts on one database at a
/// time; <c>--all-shards</c> runs the verb against each of them in turn.
/// </para>
/// </remarks>
public static class RebuildCommand
{
    /// <summary>One projection's rebuild state, with how far its shadow loop has replayed.</summary>
    private sealed record RebuildRow(ProjectionRebuildState State, long? Replayed);

    public static Command Build()
    {
        var command = new Command("rebuild",
            """
            Rebuild a projection from the beginning of the log without taking it offline.

            A rebuild replays history into a second, invisible copy of the projection while
            the live one keeps serving reads, then swaps the two. The running application
            does the replaying; this command starts, inspects and finishes it.

            Examples:
              alberto ops rebuild status
              alberto ops rebuild start my-projection --yes
              alberto ops rebuild status my-projection
              alberto ops rebuild promote my-projection --yes
              alberto ops rebuild abort my-projection --yes
            """);

        command.AddCommand(BuildStart());
        command.AddCommand(BuildStatus());
        command.AddCommand(BuildPromote());
        command.AddCommand(BuildAbort());

        return command;
    }

    private static Command BuildStart()
    {
        var command = new Command("start",
            """
            Start a rebuild: allocate a new version and set the position it must replay to.

            The application picks this up on its next poll and starts replaying. Nothing the
            projection currently serves changes until the rebuild is promoted.

            Each database replays its own log, so a sharded run starts one rebuild per shard
            and asks about each one separately — the number of events differs between them.
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID to rebuild" };
        var projectionTypeOption = new Option<string?>("--projection-type")
        {
            Description = "Projection type in the state table. Defaults to the processor ID."
        };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would happen without executing" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddOption(projectionTypeOption);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);
        var (shardOption, allShardsOption) = ShardRun.AddMutationOptions(command);

        command.SetHandler(async (string id, string? projectionType, string? url, string? schema,
            bool dryRun, bool yes, bool json, string? shard, bool allShards) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();
            if (Targets(output, () => ShardResolver.ResolveForMutation(shard, allShards, url, schema)) is not { } targets)
                return;

            await RunAsync(output, targets, async (admin, store, _) =>
            {
                var type = projectionType ?? id;
                var current = await store.GetAsync(id, type);
                var head = await admin.GetGlobalPositionAsync() ?? 0;

                if (current.IsRebuildInFlight)
                {
                    output.Error(
                        $"Processor '{id}' already has a rebuild in flight " +
                        $"(version {current.RebuildingVersion}, status {Describe(current.Status)}). " +
                        $"Promote or abort it first.");
                    return 1;
                }

                if (dryRun)
                {
                    if (json)
                    {
                        output.Json(new
                        {
                            dryRun = true,
                            action = "rebuild-start",
                            processorId = id,
                            projectionType = type,
                            activeVersion = current.ActiveVersion,
                            wouldRebuildIntoVersion = current.ActiveVersion + 1,
                            targetPosition = head,
                        });
                    }
                    else
                    {
                        output.Text(
                            $"[Dry run] Would rebuild '{id}' into version {current.ActiveVersion + 1} " +
                            $"(currently serving version {current.ActiveVersion}), replaying to position {head}.");
                    }

                    return 0;
                }

                // Asked once per database rather than once per run: the prompt states how many
                // events this shard will replay, and that number is different in each of them.
                if (!Confirm(yes, output,
                        $"[yellow]Start a rebuild of '[bold]{id}[/]'? " +
                        $"It will replay {head} events into a shadow copy.[/]",
                        $"alberto ops rebuild start {id} --yes"))
                {
                    return 0;
                }

                var state = await store.StartAsync(id, type, head);

                if (json)
                {
                    output.Json(new
                    {
                        action = "rebuild-start",
                        processorId = id,
                        projectionType = type,
                        activeVersion = state.ActiveVersion,
                        rebuildingVersion = state.RebuildingVersion,
                        targetPosition = state.TargetPosition,
                    });
                }
                else
                {
                    output.Text(
                        $"Rebuild of '{id}' started into version {state.RebuildingVersion}, " +
                        $"replaying to position {state.TargetPosition}.");
                    output.Warning(
                        "The replay runs in the application, not here. It needs a module configured with " +
                        ".WithControlLoop(loop => loop.WithRebuilds()) — without one the rebuild will sit at " +
                        "'rebuilding' forever. Watch it with: alberto ops rebuild status " + id);
                }

                return 0;
            });
        }, idArgument, projectionTypeOption, urlOption, schemaOption, dryRunOption, yesOption, jsonOption,
           shardOption, allShardsOption);

        return command;
    }

    private static Command BuildStatus()
    {
        var command = new Command("status",
            """
            Show where each projection sits in the rebuild state machine.

            Pass a processor ID to see one; omit it to see every projection that has ever
            been rebuilt.
            """);

        var idArgument = new Argument<string?>("processor-id")
        {
            Description = "Processor ID to inspect. Omit for all.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(jsonOption);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string? id, string? url, string? schema, bool json, string? shard) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();
            if (Targets(output, () => ShardResolver.ResolveForRead(shard, url, schema)) is not { } targets)
                return;

            var results = await ShardRun.ProbeAsync(targets, async (dataSource, target) =>
            {
                var admin = new PostgresAdminDataAccess(dataSource, target.Schema);
                var store = new PostgresProjectionRebuildStore(dataSource, target.Schema);

                IReadOnlyList<ProjectionRebuildState> states = id is null
                    ? await store.ListAsync()
                    : [await store.GetAsync(id, id)];

                // How far each shadow loop has got. A rebuild in flight without a checkpoint
                // has not been picked up by any application yet, which is the single most
                // common thing an operator needs to see here.
                var rows = new List<RebuildRow>(states.Count);
                foreach (var state in states)
                {
                    long? replayed = null;
                    if (state.IsRebuildInFlight)
                    {
                        // The shadow checkpoint is keyed by the version being rebuilt, so this reads
                        // the progress of *this* rebuild and never of one that came before it.
                        var checkpoint = await admin.GetSingleCheckpointAsync(
                            RebuildableProjection.ShadowProcessorId(
                                state.ProcessorId, state.RebuildingVersion!.Value));
                        replayed = checkpoint?.LastPosition;
                    }

                    rows.Add(new RebuildRow(state, replayed));
                }

                return (IReadOnlyList<RebuildRow>)rows;
            });

            var showShard = ShardRun.ShowsShard(targets);

            if (json)
            {
                output.Json(results
                    .Where(r => r.Succeeded)
                    .SelectMany(r => r.Value!.Select(row => new
                    {
                        shard = showShard ? r.Target.ShardId : null,
                        processorId = row.State.ProcessorId,
                        projectionType = row.State.ProjectionType,
                        status = row.State.Status.ToString().ToLowerInvariant(),
                        activeVersion = row.State.ActiveVersion,
                        rebuildingVersion = row.State.RebuildingVersion,
                        targetPosition = row.State.TargetPosition,
                        replayedPosition = row.Replayed,
                        startedAt = row.State.StartedAt,
                        completedAt = row.State.CompletedAt,
                    }))
                    .ToList());
            }
            else
            {
                ShardRun.Table(
                    output, targets, results,
                    ["Processor", "Status", "Active", "Rebuilding", "Progress", "Started"],
                    row =>
                    [
                        row.State.ProcessorId,
                        Describe(row.State.Status),
                        row.State.ActiveVersion.ToString(),
                        row.State.RebuildingVersion?.ToString() ?? "-",
                        DescribeProgress(row.State, row.Replayed),
                        row.State.StartedAt?.ToString("u") ?? "-",
                    ],
                    "No projection has been rebuilt.");
            }

            if (ShardRun.ReportFailures(output, results))
                Environment.Exit(1);
        }, idArgument, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }

    private static Command BuildPromote()
    {
        var command = new Command("promote",
            """
            Make a finished rebuild the version readers see, and discard the one it replaces.

            The swap is a single transaction: readers move from a complete old version to a
            complete new one with nothing in between.
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID to promote" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Promote a rebuild that has not finished replaying. Publishes incomplete state."
        };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(forceOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);
        var (shardOption, allShardsOption) = ShardRun.AddMutationOptions(command);

        command.SetHandler(async (string id, string? url, string? schema, bool force, bool yes, bool json,
            string? shard, bool allShards) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();
            if (Targets(output, () => ShardResolver.ResolveForMutation(shard, allShards, url, schema)) is not { } targets)
                return;

            var scope = ShardRun.Scope(targets);
            var prompt = force
                ? $"[yellow]Force-promote '[bold]{id}[/]'{scope} before it has finished replaying? " +
                  "Readers will see an incomplete projection.[/]"
                : $"[yellow]Promote the rebuilt version of '[bold]{id}[/]'{scope} and discard the current one?[/]";

            if (!Confirm(yes, output, prompt, $"alberto ops rebuild promote {id} --yes"))
                return;

            await RunAsync(output, targets, async (_, store, _) =>
            {
                var outcome = await store.PromoteAsync(id, force);

                if (json)
                {
                    output.Json(new
                    {
                        action = "rebuild-promote",
                        processorId = id,
                        activeVersion = outcome.State.ActiveVersion,
                        discardedVersion = outcome.DiscardedVersion,
                        forced = force,
                    });
                }
                else
                {
                    output.Text(
                        $"'{id}' now serves version {outcome.State.ActiveVersion}; " +
                        $"version {outcome.DiscardedVersion} discarded.");
                }

                return 0;
            });
        }, idArgument, urlOption, schemaOption, forceOption, yesOption, jsonOption, shardOption, allShardsOption);

        return command;
    }

    private static Command BuildAbort()
    {
        var command = new Command("abort",
            """
            Abandon a rebuild in flight and throw away the partial state it wrote.

            The version readers see is untouched, so aborting is never visible to them.
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID to abort" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);
        var (shardOption, allShardsOption) = ShardRun.AddMutationOptions(command);

        command.SetHandler(async (string id, string? url, string? schema, bool yes, bool json,
            string? shard, bool allShards) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();
            if (Targets(output, () => ShardResolver.ResolveForMutation(shard, allShards, url, schema)) is not { } targets)
                return;

            if (!Confirm(yes, output,
                    $"[yellow]Abandon the rebuild of '[bold]{id}[/]'{ShardRun.Scope(targets)} " +
                    "and discard what it has replayed so far?[/]",
                    $"alberto ops rebuild abort {id} --yes"))
            {
                return;
            }

            await RunAsync(output, targets, async (_, store, _) =>
            {
                var outcome = await store.AbortAsync(id);

                if (json)
                {
                    output.Json(new
                    {
                        action = "rebuild-abort",
                        processorId = id,
                        activeVersion = outcome.State.ActiveVersion,
                        discardedVersion = outcome.DiscardedVersion,
                    });
                }
                else
                {
                    output.Text(
                        $"Rebuild of '{id}' abandoned; version {outcome.DiscardedVersion} discarded. " +
                        $"Still serving version {outcome.State.ActiveVersion}.");
                }

                return 0;
            });
        }, idArgument, urlOption, schemaOption, yesOption, jsonOption, shardOption, allShardsOption);

        return command;
    }

    /// <summary>
    /// The databases the verb runs against, or null when it could not tell and has already
    /// reported why and set the exit code.
    /// </summary>
    private static IReadOnlyList<ShardTarget>? Targets(IOutput output, Func<IReadOnlyList<ShardTarget>> resolve)
    {
        try
        {
            return resolve();
        }
        catch (ShardSelectionException ex)
        {
            output.Error(ex.Message);
            Environment.Exit(1);
            return null;
        }
    }

    /// <summary>
    /// Opens each database the verb was pointed at, runs it there, and turns a failure into an
    /// error message and a non-zero exit code rather than a stack trace.
    /// </summary>
    /// <remarks>
    /// One shard refusing the transition does not stop the others: each database's rebuild is
    /// its own state machine, and an operator promoting across a fleet needs to know which of
    /// them moved rather than only where the run stopped.
    /// </remarks>
    private static async Task RunAsync(
        IOutput output,
        IReadOnlyList<ShardTarget> targets,
        Func<PostgresAdminDataAccess, IProjectionRebuildStore, ShardTarget, Task<int>> run)
    {
        var showShard = ShardRun.ShowsShard(targets);
        var exitCode = 0;

        foreach (var target in targets)
        {
            // Suppressed in JSON mode, where each shard emits its own object instead.
            if (showShard)
                output.Text($"[{target.ShardId}]");

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(target.ConnectionString).Build();
                var admin = new PostgresAdminDataAccess(dataSource, target.Schema);
                var store = new PostgresProjectionRebuildStore(dataSource, target.Schema);

                if (await run(admin, store, target) != 0)
                    exitCode = 1;
            }
            catch (RebuildStateException ex)
            {
                // The state machine refused the transition — an operator mistake, not a fault.
                output.Error(showShard ? $"shard '{target.ShardId}': {ex.Message}" : ex.Message);
                exitCode = 1;
            }
            catch (Exception ex)
            {
                output.Error(showShard ? $"shard '{target.ShardId}': {ex.Message}" : ex.Message);
                exitCode = 1;
            }
        }

        if (exitCode != 0)
            Environment.Exit(exitCode);
    }

    /// <summary>
    /// Asks before doing something destructive, and refuses to guess when there is nobody
    /// to ask — a rebuild verb in a deploy script must be explicit about what it wants.
    /// </summary>
    private static bool Confirm(bool yes, IOutput output, string prompt, string howToSkip)
    {
        if (yes)
            return true;

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            output.Error($"This operation requires confirmation. Add --yes to confirm.\n  {howToSkip}");
            Environment.Exit(1);
            return false;
        }

        if (AnsiConsole.Confirm(prompt, defaultValue: false))
            return true;

        output.Text("Aborted.");
        return false;
    }

    private static string Describe(RebuildStatus status) => status switch
    {
        RebuildStatus.Idle => "idle",
        RebuildStatus.Rebuilding => "rebuilding",
        RebuildStatus.Ready => "ready to promote",
        RebuildStatus.Completed => "completed",
        RebuildStatus.Aborted => "aborted",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string DescribeProgress(ProjectionRebuildState state, long? replayed)
    {
        if (!state.IsRebuildInFlight)
            return "-";

        if (state.Status is RebuildStatus.Ready)
            return "caught up";

        if (replayed is null)
            return "not started";

        return state.TargetPosition is { } target
            ? $"{replayed}/{target}"
            : replayed.ToString()!;
    }
}
