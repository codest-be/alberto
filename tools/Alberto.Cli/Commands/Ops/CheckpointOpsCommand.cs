using System.CommandLine;
using Alberto.Cli.Output;
using Alberto.Dcb.Postgres;
using Npgsql;
using Spectre.Console;

namespace Alberto.Cli.Commands.Ops;

public static class CheckpointOpsCommand
{
    public static Command Build()
    {
        var command = new Command("checkpoint", "Manage processor checkpoints");

        command.AddCommand(BuildGet());
        command.AddCommand(BuildReset());
        command.AddCommand(BuildSet());
        command.AddCommand(BuildRename());

        return command;
    }

    private static Command BuildGet()
    {
        var command = new Command("get",
            """
            Show the checkpoint for a specific processor.

            Examples:
              alberto ops checkpoint get my-processor
              alberto ops checkpoint get my-processor --json
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(jsonOption);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string id, string? url, string? schema, bool json, string? shard) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            try
            {
                var targets = ShardResolver.ResolveForRead(shard, url, schema);
                var results = await ShardRun.CollectAsync(
                    targets,
                    async admin =>
                    {
                        var checkpoint = await admin.GetSingleCheckpointAsync(id);
                        return checkpoint is null
                            ? (IReadOnlyList<CheckpointInfo>)[]
                            : [checkpoint];
                    });

                var showShard = ShardRun.ShowsShard(targets);
                var found = results.Where(r => r.Succeeded).SelectMany(r => r.Value!).Any();

                if (!found && !json)
                {
                    output.Warning($"No checkpoint found for processor '{id}'.");
                }
                else if (json)
                {
                    var payloads = results
                        .Where(r => r.Succeeded)
                        .SelectMany(r => r.Value!.Select(c => new
                        {
                            shard = showShard ? r.Target.ShardId : null,
                            c.ProcessorId,
                            c.LastPosition,
                            updatedAt = c.UpdatedAt?.ToString("O")
                        }))
                        .ToArray();

                    if (showShard)
                        output.Json(payloads);
                    else if (payloads.Length > 0)
                        output.Json(payloads[0]);
                }
                else if (showShard)
                {
                    ShardRun.Table(
                        output, targets, results,
                        ["Processor ID", "Last Position", "Updated At"],
                        c =>
                        [
                            c.ProcessorId,
                            c.LastPosition.ToString(),
                            c.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                        ],
                        $"No checkpoint found for processor '{id}'.");
                }
                else
                {
                    var checkpoint = results[0].Value![0];
                    output.Box($"Checkpoint: {id}", new Dictionary<string, string>
                    {
                        ["Processor ID"] = checkpoint.ProcessorId,
                        ["Last Position"] = checkpoint.LastPosition.ToString(),
                        ["Updated At"] = checkpoint.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                    });
                }

                if (ShardRun.ReportFailures(output, results) || !found)
                    Environment.Exit(1);
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, idArgument, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }

    private static Command BuildReset()
    {
        var command = new Command("reset",
            """
            Delete the checkpoint for a processor, triggering a full replay from the beginning.

            Examples:
              alberto ops checkpoint reset my-processor --dry-run
              alberto ops checkpoint reset my-processor --yes
              alberto ops checkpoint reset my-processor --yes --json
              alberto ops checkpoint reset my-processor --shard db2 --yes
              alberto ops checkpoint reset my-processor --all-shards --yes
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would be reset without executing" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);
        var (shardOption, allShardsOption) = ShardRun.AddMutationOptions(command);

        command.SetHandler(async (string id, string? url, string? schema, bool dryRun, bool yes, bool json, string? shard, bool allShards) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            try
            {
                var targets = ShardResolver.ResolveForMutation(shard, allShards, url, schema);

                if (!dryRun && !yes)
                {
                    if (!AnsiConsole.Profile.Capabilities.Interactive)
                    {
                        output.Error($"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops checkpoint reset {id} --yes");
                        Environment.Exit(1);
                        return;
                    }

                    // Asked once for the whole run, and it names the databases: an operator who
                    // typed --all-shards should see how many replays they are about to start.
                    var scope = ShardRun.Scope(targets);
                    var confirmed = AnsiConsole.Confirm(
                        $"[yellow]Reset checkpoint for processor '[bold]{id}[/]'{scope}? This will trigger a full replay.[/]",
                        defaultValue: false);

                    if (!confirmed)
                    {
                        output.Text("Aborted.");
                        return;
                    }
                }

                var failed = await ShardRun.ApplyAsync(output, targets, async (dataSource, target) =>
                {
                    var admin = new PostgresAdminDataAccess(dataSource, target.Schema);
                    var checkpointStore = new PostgresCheckpointStore(dataSource, target.Schema);
                    var checkpoint = await admin.GetSingleCheckpointAsync(id);
                    var previousPosition = checkpoint?.LastPosition;

                    if (dryRun)
                    {
                        if (json)
                            output.Json(new { dryRun = true, action = "reset", shard = target.ShardId, processorId = id, previousPosition });
                        else
                            output.Text($"[Dry run] Would reset checkpoint for '{id}' (currently at position {previousPosition?.ToString() ?? "none"}).");
                        return;
                    }

                    await checkpointStore.ResetAsync(id);

                    if (json)
                        output.Json(new { action = "reset", shard = target.ShardId, processorId = id, previousPosition });
                    else
                        output.Text($"Checkpoint for '{id}' has been reset (was at position {previousPosition?.ToString() ?? "none"}).");
                });

                if (failed)
                    Environment.Exit(1);
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, idArgument, urlOption, schemaOption, dryRunOption, yesOption, jsonOption, shardOption, allShardsOption);

        return command;
    }

    private static Command BuildSet()
    {
        var command = new Command("set",
            """
            Set the checkpoint position for a processor.

            Examples:
              alberto ops checkpoint set my-processor 1000 --dry-run
              alberto ops checkpoint set my-processor 1000 --yes
              alberto ops checkpoint set my-processor 1000 --yes --json
              alberto ops checkpoint set my-processor 1000 --shard db2 --yes

            A position is a per-database sequence, so on a sharded module the same number means a
            different point in each database. Set them one shard at a time.
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID" };
        var positionArgument = new Argument<long>("position") { Description = "Global position to set" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would change without executing" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddArgument(positionArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string id, long position, string? url, string? schema, bool dryRun, bool yes, bool json, string? shard) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            try
            {
                // No --all-shards here, unlike every other mutation: each database numbers its own
                // events, so one position applied to several of them would rewind each to an
                // unrelated point. A sharded module must name the shard it means.
                var targets = ShardResolver.ResolveForMutation(
                    shard, allShards: false, url, schema, supportsAllShards: false);
                var target = targets[0];

                await using var dataSource = new NpgsqlDataSourceBuilder(target.ConnectionString).Build();
                var admin = new PostgresAdminDataAccess(dataSource, target.Schema);
                var checkpointStore = new PostgresCheckpointStore(dataSource, target.Schema);
                var checkpoint = await admin.GetSingleCheckpointAsync(id);
                var previousPosition = checkpoint?.LastPosition;

                if (dryRun)
                {
                    if (json)
                        output.Json(new { dryRun = true, action = "set", shard = target.ShardId, processorId = id, previousPosition, newPosition = position });
                    else
                        output.Text($"[Dry run] Would set checkpoint for '{id}' from {previousPosition?.ToString() ?? "none"} to {position}.");
                    return;
                }

                // Lease pre-flight: warn if the processor has active leases.
                // Rewinding a running processor risks data inconsistency.
                var activeLeases = await admin.GetActiveProcessorLeasesAsync(id);
                if (activeLeases.Count > 0)
                {
                    var leaseList = string.Join(", ", activeLeases.Select(l =>
                        l.ReplicaId is not null ? $"{l.ConsumerId}/{l.ReplicaId}" : l.ConsumerId));
                    output.Warning($"WARNING: processor '{id}' has {activeLeases.Count} active lease(s) held by: {leaseList}. " +
                        "Rewinding a running processor risks data inconsistency. Stop the processor before rewinding.");
                }

                if (!yes)
                {
                    if (!AnsiConsole.Profile.Capabilities.Interactive)
                    {
                        output.Error($"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops checkpoint set {id} {position} --yes");
                        Environment.Exit(1);
                        return;
                    }

                    var confirmed = AnsiConsole.Confirm(
                        $"Set checkpoint for '[bold]{id}[/]' to position [bold]{position}[/]{ShardRun.Scope(targets)}?",
                        defaultValue: false);

                    if (!confirmed)
                    {
                        output.Text("Aborted.");
                        return;
                    }
                }

                await checkpointStore.RewindAsync(id, position);

                if (json)
                    output.Json(new { action = "set", shard = target.ShardId, processorId = id, previousPosition, newPosition = position });
                else
                    output.Text($"Checkpoint for '{id}' set to position {position} (was {previousPosition?.ToString() ?? "none"}).");
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, idArgument, positionArgument, urlOption, schemaOption, dryRunOption, yesOption, jsonOption, shardOption);

        return command;
    }

    private static Command BuildRename()
    {
        var command = new Command("rename",
            """
            Rename a checkpoint by copying its position to a new processor id and removing the old one.

            Use this after renaming a handler class to carry the stored position to the new derived id,
            preventing a full replay from the beginning.

            Examples:
              alberto ops checkpoint rename --from OldHandlerName --to NewHandlerName
              alberto ops checkpoint rename --module orders --from OldHandlerName --to NewHandlerName
              alberto ops checkpoint rename --from OldHandlerName --to NewHandlerName --all-shards
            """);

        var moduleOption = new Option<string?>("--module") { Description = "Module key (for context; shown in startup warnings)" };
        var fromOption = new Option<string?>("--from") { Description = "Old processor id (the orphaned checkpoint key)" };
        var toOption = new Option<string?>("--to") { Description = "New processor id (the current handler's derived id)" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };

        command.AddOption(moduleOption);
        command.AddOption(fromOption);
        command.AddOption(toOption);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        var (shardOption, allShardsOption) = ShardRun.AddMutationOptions(command);

        command.SetHandler(async (string? module, string? from, string? to, string? url, string? schema, string? shard, bool allShards) =>
        {
            IOutput output = new HumanOutput();

            if (string.IsNullOrWhiteSpace(from))
            {
                output.Error("--from is required.");
                Environment.Exit(1);
                return;
            }

            if (string.IsNullOrWhiteSpace(to))
            {
                output.Error("--to is required.");
                Environment.Exit(1);
                return;
            }

            try
            {
                // A renamed handler is renamed everywhere, so this is the one mutation an operator
                // usually does want against every database at once.
                var targets = ShardResolver.ResolveForMutation(shard, allShards, url, schema);

                var failed = await ShardRun.ApplyAsync(output, targets, async (dataSource, target) =>
                {
                    var data = new PostgresAdminDataAccess(dataSource, target.Schema);

                    var result = await data.RenameCheckpointAsync(from, to);
                    switch (result.Status)
                    {
                        case CheckpointRenameStatus.Renamed:
                            output.Text($"Renamed checkpoint '{from}' to '{to}' at position {result.Position}.");
                            return;

                        case CheckpointRenameStatus.SourceNotFound:
                            {
                                var moduleHint = module is not null ? $" in module '{module}'" : string.Empty;
                                throw new InvalidOperationException(
                                    $"No checkpoint named '{from}' exists{moduleHint}.");
                            }

                        case CheckpointRenameStatus.DestinationExists:
                            {
                                var moduleHint = module is not null ? $" --module {module}" : string.Empty;
                                var shardHint = target.ShardId is not null ? $" --shard {target.ShardId}" : string.Empty;
                                throw new InvalidOperationException(
                                    $"'{to}' already has a checkpoint at position {result.Position}. " +
                                    "Reset it first if you really mean to overwrite it: " +
                                    $"alberto ops checkpoint reset {to} --yes{moduleHint}{shardHint}");
                            }

                        case CheckpointRenameStatus.SameProcessorId:
                            throw new InvalidOperationException(
                                "--from and --to must name different processor ids.");

                        default:
                            throw new InvalidOperationException(
                                $"Unknown checkpoint rename result '{result.Status}'.");
                    }
                });

                if (failed)
                    Environment.Exit(1);
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, moduleOption, fromOption, toOption, urlOption, schemaOption, shardOption, allShardsOption);

        return command;
    }
}
