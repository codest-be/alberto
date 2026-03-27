# Plan 04: CLI Tool

## Goal
Create an `alberto` CLI tool for operational management — status inspection, dead letter management, checkpoint manipulation, and rebuild triggering. Modeled after the TS `@alberto/cli` but as a .NET global tool.

## Reference Implementation (TS)

`packages/cli/`:
- **Diagnostic commands** (read-only): `status`, `processor <id>`, `dead-letters`, `events`, `types`, `tags`, `tenants`, `outbox`, `schema`, `explain`
- **Ops commands** (mutating): `ops dead-letters dismiss/retry`, `ops outbox retry/purge`, `ops checkpoint get/reset/set`, `ops rebuild <id>`, `ops audit`
- Connection resolution: `--url` flag → env vars → `.alberto/config.yaml` → defaults
- Output modes: human-readable (default) or `--json`
- Dry-run support via `--dry-run` for mutating operations
- Confirmation prompts via `--yes` for destructive operations

## Implementation Plan

### Step 1: Create `Alberto.Cli` project

New console app under `tools/Alberto.Cli/`. Use `System.CommandLine` for argument parsing.

Package as a .NET global tool:
```xml
<PackAs>DotNetCliTool</PackAs>
<ToolCommandName>alberto</ToolCommandName>
```

### Step 2: Connection resolution

```csharp
public static class ConnectionResolver
{
    public static string Resolve(string? urlOption)
    {
        // 1. --url flag
        if (!string.IsNullOrEmpty(urlOption)) return urlOption;

        // 2. ALBERTO_URL env var
        var envUrl = Environment.GetEnvironmentVariable("ALBERTO_URL");
        if (!string.IsNullOrEmpty(envUrl)) return envUrl;

        // 3. .alberto/config.yaml (walk up directory tree)
        var config = FindConfig();
        if (config?.ConnectionUrl is not null) return config.ConnectionUrl;

        // 4. DATABASE_URL env var
        var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrEmpty(dbUrl)) return dbUrl;

        // 5. Default
        return "Host=localhost;Database=postgres";
    }
}
```

Config file format (`.alberto/config.yaml`):
```yaml
connection:
  url: "Host=localhost;Database=mydb"
schema: orders
operator: bjorn
```

### Step 3: Output formatting

```csharp
public interface IOutput
{
    void Text(string text);
    void Table(string[] headers, IEnumerable<string[]> rows);
    void Box(string title, Dictionary<string, string> fields);
    void Json(object data);
    void Warning(string message);
    void Error(string message);
}
```

Two implementations: `HumanOutput` (Spectre.Console for colors/tables) and `JsonOutput` (System.Text.Json to stdout, human text to stderr).

### Step 4: Diagnostic commands

Reuse `IAdminDataAccess` and `IAdminQueryService` interfaces from `Alberto.Dcb.Admin`. The CLI connects directly to PostgreSQL — no running application needed.

**`alberto status [--schema <name>]`**
- Show global position, processor count, dead letter count
- Table of processors with status, lag, last updated
- Warnings for lagging/stalled processors

**`alberto processor <id> [--schema <name>]`**
- Detailed processor info: checkpoint, lag, handled event types, dead letter count
- Status classification: current / healthy / catching-up / lagging / stalled

**`alberto dead-letters [--processor <id>] [--type <type>] [--limit <n>]`**
- List dead letter entries with error messages
- Group by error pattern

**`alberto events [--tenant <id>] [--type <type>] [--tag <tag>] [--after <pos>] [--limit <n>] [--tail]`**
- Stream browser — display events with formatted output
- `--tail` mode: poll for new events continuously

**`alberto checkpoints [--schema <name>]`**
- List all checkpoints with last position and last updated

### Step 5: Ops commands (mutating)

**`alberto ops checkpoint get <processor-id>`**
**`alberto ops checkpoint reset <processor-id> [--yes]`**
**`alberto ops checkpoint set <processor-id> <position> [--yes]`**

**`alberto ops dead-letters dismiss [--processor <id>] [--all] [--dry-run] [--yes]`**
**`alberto ops dead-letters retry [--processor <id>] [--dry-run] [--yes]`**

**`alberto ops rebuild <processor-id> [--from <position>] [--yes]`**
- Resets checkpoint (optionally to a specific position)
- Logs to audit trail

**`alberto ops audit [--limit <n>]`**
- Lists recent admin operations from audit log

### Step 6: Direct database access layer

The CLI needs to talk directly to PostgreSQL without the running application. Create a lightweight data access class:

```csharp
internal sealed class CliDataAccess : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;

    public Task<long> GetGlobalPositionAsync(CancellationToken ct);
    public Task<IReadOnlyList<ProcessorInfo>> GetProcessorsAsync(CancellationToken ct);
    public Task<IReadOnlyList<CheckpointInfo>> GetCheckpointsAsync(CancellationToken ct);
    public Task<IReadOnlyList<DeadLetterInfo>> GetDeadLettersAsync(...);
    public Task<IReadOnlyList<EventInfo>> GetEventsAsync(...);
    public Task ResetCheckpointAsync(string processorId, CancellationToken ct);
    public Task SetCheckpointAsync(string processorId, long position, CancellationToken ct);
    // ... etc
}
```

## Files to Create

- `tools/Alberto.Cli/Alberto.Cli.csproj`
- `tools/Alberto.Cli/Program.cs`
- `tools/Alberto.Cli/ConnectionResolver.cs`
- `tools/Alberto.Cli/ConfigFile.cs`
- `tools/Alberto.Cli/Output/IOutput.cs`
- `tools/Alberto.Cli/Output/HumanOutput.cs`
- `tools/Alberto.Cli/Output/JsonOutput.cs`
- `tools/Alberto.Cli/Data/CliDataAccess.cs`
- `tools/Alberto.Cli/Commands/StatusCommand.cs`
- `tools/Alberto.Cli/Commands/ProcessorCommand.cs`
- `tools/Alberto.Cli/Commands/DeadLettersCommand.cs`
- `tools/Alberto.Cli/Commands/EventsCommand.cs`
- `tools/Alberto.Cli/Commands/CheckpointsCommand.cs`
- `tools/Alberto.Cli/Commands/Ops/CheckpointOpsCommand.cs`
- `tools/Alberto.Cli/Commands/Ops/DeadLetterOpsCommand.cs`
- `tools/Alberto.Cli/Commands/Ops/RebuildCommand.cs`
- `tools/Alberto.Cli/Commands/Ops/AuditCommand.cs`

## Dependencies

- `System.CommandLine` — argument parsing
- `Spectre.Console` — rich terminal output (tables, colors, boxes)
- `Npgsql` — direct PostgreSQL access
- `YamlDotNet` — config file parsing (optional, could use JSON)

## Acceptance Criteria

- [ ] `alberto status` shows schema overview with processor table
- [ ] `alberto processor <id>` shows detailed processor info
- [ ] `alberto dead-letters` lists dead letter entries
- [ ] `alberto events --tail` polls for new events
- [ ] `alberto ops checkpoint reset` resets with confirmation prompt
- [ ] `alberto ops rebuild` resets checkpoint and logs to audit
- [ ] `--json` flag outputs machine-readable JSON
- [ ] `--dry-run` shows what would happen without executing
- [ ] Connection resolution works via flag, env var, config file, and defaults
