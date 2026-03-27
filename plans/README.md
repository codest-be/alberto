# Alberto .NET — Port Plans from TS

Features ported from `alberto-ts` to the .NET framework. Each plan is self-contained and can be implemented by an independent agent.

## Dependency Graph

Some plans have dependencies on others. Respect this ordering:

```
Independent (can start immediately):
  05-query-for-shortcut
  06-decider-evolver
  09-audit-trail
  13-lifecycle-callbacks

Depends on nothing but benefits from 13:
  01-batch-projection-processing  (13 adds callbacks to batch path)

Depends on 01:
  12-declare-projection-api       (uses IBatchableProcessor from 01)

Depends on nothing but logically pairs with 09:
  10-dead-letter-retry-rewind

Independent infrastructure:
  02-fencing-tokens
  03-outbox-messaging
  08-middleware-composition
  11-consistent-hash-ring

Architectural (touches everything — do early or late, not middle):
  07-optional-tenancy-decorator   (breaking interface change — ideally wave 1 or wave 4)

Depends on most of the above:
  04-cli-tool                     (uses 09 audit, 10 rewind, consumes all data)
```

## Suggested Implementation Order

### Wave 0 — Architectural foundation (optional: do first if committing to the break)
- **07** Optional tenancy decorator (~6 hr, breaking change — easier if done before other plans touch the interface)

### Wave 1 — No dependencies, quick wins
- **05** Query.For() shortcut (~30 min)
- **13** Lifecycle callbacks (~1 hr)
- **06** Decider + Evolver (~2 hr)
- **09** Audit trail (~2 hr)

### Wave 2 — Core improvements
- **01** Batch projection processing (~3 hr)
- **08** Middleware composition (~3 hr)
- **02** Fencing tokens (~2 hr)
- **10** Dead letter retry rewind (~2 hr)

### Wave 3 — Larger features
- **12** DeclareProjection API (~4 hr, depends on 01)
- **11** Consistent hash ring (~3 hr)
- **03** Outbox/messaging (~4 hr)

### Wave 4 — Capstone
- **04** CLI tool (~6 hr, benefits from everything above)

## Plan Index

| # | Plan | Scope | Category |
|---|------|-------|----------|
| 01 | [Batch Projection Processing](01-batch-projection-processing.md) | `AsyncProjection`, `PollingConsumer` | Performance |
| 02 | [Fencing Tokens](02-fencing-tokens.md) | `CheckpointStore`, `PollingConsumer` | Correctness |
| 03 | [Outbox / Messaging](03-outbox-messaging.md) | New `Alberto.Dcb.Messaging` package | Feature |
| 04 | [CLI Tool](04-cli-tool.md) | New `tools/Alberto.Cli` project | Tooling |
| 05 | [Query.For() Shortcut](05-query-for-shortcut.md) | `DcbQuery.cs` | DX |
| 06 | [Decider + Evolver](06-decider-evolver.md) | New files in `Alberto.Dcb` | DX |
| 07 | [Optional Tenancy Decorator](07-optional-tenancy-decorator.md) | `IEventStoreBackend`, PostgreSQL schema, everything | Architecture |
| 08 | [Middleware Composition](08-middleware-composition.md) | `PollingConsumer`, consume pipeline | Architecture |
| 09 | [Audit Trail](09-audit-trail.md) | `AdminQueryService`, new migration | Operations |
| 10 | [Dead Letter Retry Rewind](10-dead-letter-retry-rewind.md) | `AdminQueryService`, dead letters | Operations |
| 11 | [Consistent Hash Ring](11-consistent-hash-ring.md) | `PollingConsumer`, tenant distribution | Infrastructure |
| 12 | [DeclareProjection API](12-declare-projection-api.md) | Projection system (replaces old API) | Architecture |
| 13 | [Lifecycle Callbacks](13-lifecycle-callbacks.md) | `PollingConsumer` | Testing/Observability |

## Migration Numbers

Plans requiring PostgreSQL migrations use reserved numbers:
- 012: Fencing tokens (plan 02)
- 013: Outbox (plan 03)
- 014: Audit log (plan 09)
- 015: Dead letter position column (plan 10)
- 016: Tenant assignments (plan 11)
