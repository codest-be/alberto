# Plan 09: Audit Trail for Admin Operations

## Goal
Log all mutating admin operations (checkpoint resets, rebuilds, dead letter dismissals, etc.) to a `processor_audit_log` table. This provides accountability and debugging history.

## Reference Implementation (TS)

`packages/operations/src/operations.ts`:
- Every mutating operation writes to `admin.processor_audit_log`
- Fields: `id`, `operator` (who), `action` (what), `processor_id`, `details` (JSONB), `created_at`
- Actions include: `checkpoint.reset`, `checkpoint.set`, `rebuild.start`, `dead-letters.dismiss`, `dead-letters.retry`, `outbox.retry`, `outbox.purge`

## Implementation Plan

### Step 1: Migration — create audit log table

New migration (add to PostgreSQL migrations):

```sql
CREATE TABLE IF NOT EXISTS {schema}.processor_audit_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    operator TEXT NOT NULL DEFAULT 'system',
    action TEXT NOT NULL,
    processor_id TEXT,
    details JSONB NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_{schema}_audit_log_created
    ON {schema}.processor_audit_log (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_{schema}_audit_log_processor
    ON {schema}.processor_audit_log (processor_id, created_at DESC);
```

### Step 2: Audit store interface

```csharp
namespace Alberto.Dcb.Admin;

public record AuditEntry(
    Guid Id,
    string Operator,
    string Action,
    string? ProcessorId,
    Dictionary<string, object?> Details,
    DateTimeOffset CreatedAt);

public interface IAuditStore
{
    Task LogAsync(string @operator, string action, string? processorId,
        Dictionary<string, object?>? details = null, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> ListAsync(
        string? processorId = null, int limit = 50,
        CancellationToken ct = default);
}
```

### Step 3: PostgreSQL implementation

```csharp
public sealed class PostgresAuditStore : IAuditStore
{
    // INSERT into processor_audit_log
    // SELECT with optional processor_id filter, ORDER BY created_at DESC
}
```

### Step 4: Integrate with AdminQueryService

Add audit logging to existing admin operations:

```csharp
// In StartRebuildAsync:
await _auditStore.LogAsync(operatorName, "rebuild.start", processorId,
    new() { ["clearState"] = clearState, ["targetPosition"] = targetPosition });

// In ResetCheckpointAsync:
await _auditStore.LogAsync(operatorName, "checkpoint.reset", processorId);

// In SetCheckpointAsync:
await _auditStore.LogAsync(operatorName, "checkpoint.set", processorId,
    new() { ["position"] = position });

// In RemoveDeadLetterAsync:
await _auditStore.LogAsync(operatorName, "dead-letter.dismiss", null,
    new() { ["deadLetterId"] = id });

// In RetryDeadLetterAsync:
await _auditStore.LogAsync(operatorName, "dead-letter.retry", deadLetter.ProcessorId,
    new() { ["deadLetterId"] = id, ["success"] = result.Success });
```

### Step 5: Operator context

The operator name needs to come from somewhere. Options:
- HTTP header: `X-Alberto-Operator` (for admin API calls)
- CLI: `--operator` flag or config file
- Default: `"system"` for automated operations

Add to `AdminOptions`:
```csharp
public string DefaultOperator { get; set; } = "system";
```

### Step 6: Admin API endpoint

Add endpoint to list audit entries:

```
GET /{basePath}/{moduleKey}/api/audit?processorId=...&limit=50
```

### Step 7: Admin REST endpoint for audit listing

In the existing admin endpoints, add:

```csharp
group.MapGet("/audit", async (
    IAdminQueryService service,
    string? processorId,
    int? limit,
    CancellationToken ct) =>
{
    var entries = await service.GetAuditLogAsync(processorId, limit ?? 50, ct);
    return Results.Ok(entries);
});
```

## Files to Create

- `src/Alberto.Dcb.Admin/IAuditStore.cs` + `AuditEntry.cs`
- `src/Alberto.Dcb.Postgres/PostgresAuditStore.cs`
- `src/Alberto.Dcb.Postgres/Migrations/014_audit_log.sql`

## Files to Modify

- `src/Alberto.Dcb.Admin/Internal/AdminQueryService.cs` — add audit logging to mutating operations
- `src/Alberto.Dcb.Admin/Internal/IAdminQueryService.cs` — add `GetAuditLogAsync`
- `src/Alberto.Dcb.Admin/Endpoints/` — add audit endpoint (new file or extend SystemEndpoints)
- `src/Alberto.Dcb.Postgres/PostgresBuilderExtensions.cs` — register `PostgresAuditStore`

## Acceptance Criteria

- [ ] All mutating admin operations create audit log entries
- [ ] Audit entries include operator, action, processor ID, and details
- [ ] Audit log queryable by processor ID
- [ ] REST endpoint returns audit entries
- [ ] Default operator is "system", overridable via options
