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
/// This command records operator intent. The replay and every completion transition are
/// carried out by the running application's rebuild coordinator.
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

    /// <summary>The immutable facts captured before a rebuild-start mutation is confirmed.</summary>
    private sealed record RebuildStartPlan(ProjectionRebuildState State, long Head);

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

            Each database replays its own log, so a sharded run first checks every shard and
            captures its head, then asks once before starting any of them.
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
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;
                var targets = session.MutationTargets(shard, allShards, url, schema);
                var type = projectionType ?? id;
                var showShard = ShardRun.ShowsShard(targets);

                // A destructive fan-out is planned in full before the first write. An unreachable
                // shard or a rebuild already in flight therefore cannot leave the fleet half-started.
                return await PlannedMutation.RunAsync(
                    targets,
                    async target =>
                    {
                        await using var dataSource =
                            new NpgsqlDataSourceBuilder(target.ConnectionString).Build();
                        var admin = new PostgresAdminDataAccess(dataSource, target.Schema);
                        var store = new PostgresProjectionRebuildStore(dataSource, target.Schema);
                        var current = await store.GetAsync(id, type);
                        var head = await admin.GetGlobalPositionAsync() ?? 0;
                        return new RebuildStartPlan(current, head);
                    },
                    (target, failure) => output.Error(
                        showShard ? $"shard '{target.ShardId}': {failure.Message}" : failure.Message),
                    plans =>
                    {
                        var conflicts = plans
                            .Where(plan => plan.Plan.State.IsRebuildInFlight)
                            .ToArray();
                        foreach (var conflict in conflicts)
                        {
                            var current = conflict.Plan.State;
                            var prefix = conflict.Target.ShardId is null
                                ? string.Empty
                                : $"shard '{conflict.Target.ShardId}': ";
                            output.Error(
                                $"{prefix}Processor '{id}' already has a rebuild in flight " +
                                $"(version {current.RebuildingVersion}, status {Describe(current.Status)}). " +
                                "Promote or abort it first.");
                        }

                        if (conflicts.Length > 0)
                            return 1;

                        if (dryRun)
                        {
                            foreach (var plan in plans)
                            {
                                var current = plan.Plan.State;
                                if (json)
                                {
                                    output.Json(new
                                    {
                                        dryRun = true,
                                        action = "rebuild-start",
                                        shard = showShard ? plan.Target.ShardId : null,
                                        processorId = id,
                                        projectionType = type,
                                        activeVersion = current.ActiveVersion,
                                        wouldRebuildIntoVersion = current.LastAllocatedVersion + 1,
                                        targetPosition = plan.Plan.Head,
                                    });
                                }
                                else
                                {
                                    if (showShard)
                                        output.Text($"[{plan.Target.ShardId}]");
                                    output.Text(
                                        $"[Dry run] Would rebuild '{id}' into version {current.LastAllocatedVersion + 1} " +
                                        $"(currently serving version {current.ActiveVersion}), replaying to position {plan.Plan.Head}.");
                                }
                            }

                            return 0;
                        }

                        var capturedHeads = targets.Count == 1
                            ? $"It will replay {plans[0].Plan.Head} events into a shadow copy."
                            : "It will replay every shard to its captured head (" +
                              string.Join(", ", plans.Select(plan =>
                                  $"{plan.Target.ShardId}: {plan.Plan.Head}")) + ").";
                        return session.Confirm(
                            yes,
                            $"[yellow]Start a rebuild of '[bold]{id}[/]'{ShardRun.Scope(targets)}? " +
                            $"{capturedHeads}[/]",
                            "This operation requires confirmation. Add --yes to confirm.\n" +
                            $"  alberto ops rebuild start {id} --yes");
                    },
                    async (target, plan) =>
                    {
                        if (showShard && !json)
                            output.Text($"[{target.ShardId}]");

                        await using var dataSource =
                            new NpgsqlDataSourceBuilder(target.ConnectionString).Build();
                        var store = new PostgresProjectionRebuildStore(dataSource, target.Schema);
                        var state = await store.StartAsync(id, type, plan.Head);
                        if (json)
                        {
                            output.Json(new
                            {
                                action = "rebuild-start",
                                shard = showShard ? target.ShardId : null,
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
                    },
                    (target, failure) => output.Error(
                        showShard ? $"shard '{target.ShardId}': {failure.Message}" : failure.Message));
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
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;
                var targets = session.ReadTargets(shard, url, schema);

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
                            lastAllocatedVersion = row.State.LastAllocatedVersion,
                            requestedAction = row.State.RequestedAction?.ToString().ToLowerInvariant(),
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
                        ["Processor", "Status", "Requested", "Active", "Rebuilding", "Progress", "Started"],
                        row =>
                        [
                            row.State.ProcessorId,
                            Describe(row.State.Status),
                            Describe(row.State.RequestedAction),
                            row.State.ActiveVersion.ToString(),
                            row.State.RebuildingVersion?.ToString() ?? "-",
                            DescribeProgress(row.State, row.Replayed),
                            row.State.StartedAt?.ToString("u") ?? "-",
                        ],
                        "No projection has been rebuilt.");
                }

                return ShardRun.ReportFailures(output, results) ? 1 : 0;
                });
        }, idArgument, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }

    private static Command BuildPromote()
    {
        var command = new Command("promote",
            """
            Ask the running application to make a finished rebuild the version readers see.

            The coordinator stops the shadow loop, verifies its checkpoint is current, performs
            the version swap, hands off the checkpoint, and clears superseded external state.
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID to promote" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Request promotion before the original target; never publishes behind the live processor."
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
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;
                var targets = session.MutationTargets(shard, allShards, url, schema);

                var scope = ShardRun.Scope(targets);
                var prompt = force
                    ? $"[yellow]Request early promotion of '[bold]{id}[/]'{scope}? " +
                      "The coordinator will still wait until the shadow is current.[/]"
                    : $"[yellow]Request promotion of the rebuilt version of '[bold]{id}[/]'{scope}?[/]";

                if (session.Confirm(yes, prompt,
                        "This operation requires confirmation. Add --yes to confirm.\n" +
                        $"  alberto ops rebuild promote {id} --yes") is { } code)
                {
                    return code;
                }

                return await RunAsync(output, targets, async (_, store, _) =>
                {
                    var state = await store.RequestPromotionAsync(id, force);

                    if (json)
                    {
                        output.Json(new
                        {
                            action = "rebuild-promote",
                            processorId = id,
                            status = "requested",
                            activeVersion = state.ActiveVersion,
                            rebuildingVersion = state.RebuildingVersion,
                            forced = force,
                        });
                    }
                    else
                    {
                        output.Text(
                            $"Promotion of '{id}' requested. The running application will complete " +
                            "the safe handoff on its next coordinator poll.");
                    }

                    return 0;
                });
            });
        }, idArgument, urlOption, schemaOption, forceOption, yesOption, jsonOption, shardOption, allShardsOption);

        return command;
    }

    private static Command BuildAbort()
    {
        var command = new Command("abort",
            """
            Ask the running application to abandon a rebuild in flight.

            The coordinator stops the shadow loop before discarding its partial state. The
            version readers see is untouched.
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
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;
                var targets = session.MutationTargets(shard, allShards, url, schema);

                if (session.Confirm(yes,
                        $"[yellow]Abandon the rebuild of '[bold]{id}[/]'{ShardRun.Scope(targets)} " +
                        "and discard what it has replayed so far?[/]",
                        "This operation requires confirmation. Add --yes to confirm.\n" +
                        $"  alberto ops rebuild abort {id} --yes") is { } code)
                {
                    return code;
                }

                return await RunAsync(output, targets, async (_, store, _) =>
                {
                    var state = await store.RequestAbortAsync(id);

                    if (json)
                    {
                        output.Json(new
                        {
                            action = "rebuild-abort",
                            processorId = id,
                            status = "requested",
                            activeVersion = state.ActiveVersion,
                            rebuildingVersion = state.RebuildingVersion,
                        });
                    }
                    else
                    {
                        output.Text(
                            $"Abort of '{id}' requested. The running application will stop the " +
                            "shadow loop and discard its state on the next coordinator poll.");
                    }

                    return 0;
                });
            });
        }, idArgument, urlOption, schemaOption, yesOption, jsonOption, shardOption, allShardsOption);

        return command;
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
    private static async Task<int> RunAsync(
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

        return exitCode;
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

    private static string Describe(RebuildOperatorAction? action) => action switch
    {
        RebuildOperatorAction.Promote => "promote",
        RebuildOperatorAction.ForcePromote => "force promote",
        RebuildOperatorAction.Abort => "abort",
        _ => "-",
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
