# Async Processing Architecture

This document describes how events flow through the async processing pipeline in Alberto, from being written to the event store through projection/reaction processing to real-time admin updates.

## High-Level Flow

```
Event Appended to Store
    ↓
PostgreSQL Event Table
    ↓
PollingConsumer Polls Global Stream
    ↓
Events Routed to Processors (AsyncProjection/AsyncReactor)
    ↓
Processor Applies Business Logic
    ↓
State Updated in EF or PostgreSQL
    ↓
Checkpoint Saved (via CachingCheckpointStore)
    ↓
PostgreSQL NOTIFY Triggers
    ↓
PostgresAdminListener Receives Notification
    ↓
Admin Dashboard Updated (Real-time)
```

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                         ASYNC PROCESSING ARCHITECTURE                           │
└─────────────────────────────────────────────────────────────────────────────────┘

                              ┌──────────────────┐
                              │   APPLICATION    │
                              │  (Orders API)    │
                              └────────┬─────────┘
                                       │ Append Events
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│                           POSTGRESQL EVENT STORE                                │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────────────────┐ │
│  │  events table   │    │  checkpoints    │    │      NOTIFY Triggers        │ │
│  │  (global log)   │    │    table        │    │  • {schema}_events          │ │
│  │                 │    │                 │    │  • {schema}_checkpoints     │ │
│  │  position: 1001 │    │  processor:pos  │    │  • {schema}_dead_letters    │ │
│  └────────┬────────┘    └────────▲────────┘    └──────────────┬──────────────┘ │
└───────────┼──────────────────────┼────────────────────────────┼─────────────────┘
            │                      │                            │
            │ Poll every 100ms     │ Batch flush (1s)           │ LISTEN
            ▼                      │                            ▼
┌───────────────────────────────────────────────────┐  ┌────────────────────────┐
│              POLLING CONSUMER                     │  │ PostgresAdminListener  │
│  ┌─────────────────────────────────────────────┐  │  │    (BackgroundService) │
│  │          Main Polling Loop                  │  │  │                        │
│  │  1. Get global position                     │  │  │  • Debounce 100ms      │
│  │  2. Classify processors (active/rebuilding) │  │  │  • Batch notifications │
│  │  3. Fetch batch of events (100)             │  │  │  • Query changed data  │
│  │  4. Route to relevant processors            │  │  └───────────┬────────────┘
│  │  5. Adaptive backoff if no events           │  │              │
│  └─────────────────────────────────────────────┘  │              ▼
│                                                   │  ┌────────────────────────┐
│  ┌─────────────────┐  ┌─────────────────────────┐│  │  HotChocolate GraphQL  │
│  │ Active Processor│  │   Rebuild Task          ││  │    Subscriptions       │
│  │ lag < 1000      │  │   (independent loop)    ││  │                        │
│  │                 │  │   batch size: 1000      ││  │  • ProcessorUpdated    │
│  │ Processes with  │  │   catches up then       ││  │  • CheckpointUpdated   │
│  │ main loop       │  │   rejoins main loop     ││  │  • DeadLetterAdded     │
│  └────────┬────────┘  └─────────────────────────┘│  └───────────┬────────────┘
└───────────┼──────────────────────────────────────┘              │
            │                                                      │ WebSocket
            ▼                                                      ▼
┌─────────────────────────────────────────────────┐   ┌────────────────────────┐
│              EVENT PROCESSORS                    │   │   Angular Admin Web    │
│                                                  │   │                        │
│  ┌────────────────────────────────────────────┐ │   │  Real-time dashboard   │
│  │         AsyncProjection<TState>            │ │   └────────────────────────┘
│  │                                            │ │
│  │  ┌──────────────┐    ┌─────────────────┐  │ │
│  │  │ Load State   │───▶│  Apply Event    │  │ │
│  │  │ (per docId)  │    │  (pure func)    │  │ │
│  │  └──────────────┘    └───────┬─────────┘  │ │
│  │                              │            │ │
│  │                 ┌────────────┼────────────┤ │
│  │                 ▼            ▼            ▼ │
│  │            Set(state)   Delete    Unchanged │
│  │                 │            │              │
│  │                 ▼            ▼              │
│  │           ┌──────────────────────┐         │ │
│  │           │   State Store        │         │ │
│  │           │   (EfStateStore)     │         │ │
│  │           └──────────────────────┘         │ │
│  └────────────────────────────────────────────┘ │
│                                                  │
│  ┌────────────────────────────────────────────┐ │
│  │         AsyncReactor<TReactor>             │ │
│  │                                            │ │
│  │  ┌──────────────────┐                     │ │
│  │  │ Reflection-based │  Side effects:      │ │
│  │  │ dispatch to      │  • Send emails      │ │
│  │  │ IReact<TEvent>   │  • Update analytics │ │
│  │  │ handlers         │  • Call APIs        │ │
│  │  └──────────────────┘                     │ │
│  └────────────────────────────────────────────┘ │
│                                                  │
│  ┌────────────────────────────────────────────┐ │
│  │      BatchedEfProjection<THandler>         │ │
│  │                                            │ │
│  │  Multiple events ──▶ Single SaveChanges   │ │
│  │  (Change tracker accumulates, flush once)  │ │
│  └────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────┐
│          CHECKPOINT MANAGEMENT                   │
│                                                  │
│  ┌────────────────────────────────────────────┐ │
│  │         CachingCheckpointStore             │ │
│  │                                            │ │
│  │  Write: Cache immediately, mark dirty     │ │
│  │  Read:  Cache hit → return                │ │
│  │         Cache miss → DB → cache           │ │
│  │  Flush: Timer (1s) → batch write to DB    │ │
│  │                                            │ │
│  │  ┌──────────┐     ┌──────────┐            │ │
│  │  │ _cache   │     │ _dirty   │            │ │
│  │  │ proc:pos │     │ proc:pos │            │ │
│  │  └──────────┘     └────┬─────┘            │ │
│  │                        │ every 1s         │ │
│  │                        ▼                  │ │
│  │              PostgresCheckpointStore      │ │
│  └────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────┐
│             READ MODEL STORAGE                   │
│                                                  │
│  ┌─────────────────────────────────────────────┐│
│  │          EfStateStore<TEntity>              ││
│  │                                             ││
│  │  • Pooled DbContext factory                 ││
│  │  • Batch load-then-save pattern             ││
│  │  • Tenant isolation (TenantId filter)       ││
│  │  • Owned type handling (JSON columns)       ││
│  └─────────────────────────────────────────────┘│
│                                                  │
│  ┌─────────────────────────────────────────────┐│
│  │        OrdersDbContext Tables               ││
│  │                                             ││
│  │  • order_summaries (projection output)     ││
│  │  • order_line_items (JSON column)          ││
│  └─────────────────────────────────────────────┘│
└─────────────────────────────────────────────────┘
```

## Error Handling Flow

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              ERROR HANDLING FLOW                                │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│   Event Processing Failed                                                       │
│          │                                                                      │
│          ▼                                                                      │
│   ┌─────────────┐     ┌─────────────┐     ┌─────────────────────┐             │
│   │  Retry 1    │────▶│  Retry 2    │────▶│  Retry 3 (max)      │             │
│   │  (1s delay) │     │  (1s delay) │     │                     │             │
│   └─────────────┘     └─────────────┘     └──────────┬──────────┘             │
│                                                       │                        │
│                                           ┌───────────▼───────────┐            │
│                                           │    Dead Letter        │            │
│                                           │    (if enabled)       │            │
│                                           │                       │            │
│                                           │  • Stores failed evt  │            │
│                                           │  • Triggers NOTIFY    │            │
│                                           │  • Skips to next evt  │            │
│                                           └───────────────────────┘            │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
```

## Key Components

### PollingConsumer

The central coordinator that continuously polls the event store and routes events to processors.

**Location:** `src/Alberto.Dcb/Subscriptions/PollingConsumer.cs`

**Responsibilities:**
- Polls event store at configurable intervals (default: 100ms)
- Manages multiple processors with independent checkpoints
- Classifies processors as "active" (caught-up) or "rebuilding" (far behind)
- Handles automatic rebuild when processors fall behind threshold
- Routes events only to processors that handle specific event types

**Classification Logic:**
```csharp
// Processors are classified based on lag threshold
if (lag > rebuildThreshold)  // Default: 1000 events
{
    processor.IsRebuilding = true;  // Run independently
}
else
{
    activeCheckpoints[processorId] = position;  // Included in main loop
}
```

### AsyncProjection

Transforms events into read model state using pure functions.

**Location:** `src/Alberto.Dcb/Subscriptions/AsyncProjection.cs`

**Processing Flow:**
1. Extract document ID from event
2. Get tenant-specific state store
3. Load current state (or create new)
4. Apply event (pure function) → returns `Set`, `Delete`, or `Unchanged`
5. Persist result to state store

### AsyncReactor

Handles side effects in response to events (emails, notifications, external API calls).

**Location:** `src/Alberto.Dcb/Subscriptions/AsyncReactor.cs`

**Features:**
- Reflection-based dispatch to `IReact<TEvent>` handlers
- No state persistence (fire-and-forget side effects)
- Scans reactor type for implemented interfaces at startup

### CachingCheckpointStore

Optimizes checkpoint persistence by batching writes.

**Location:** `src/Alberto.Dcb/Subscriptions/CachingCheckpointStore.cs`

**Strategy:**
- **Write path:** Update in-memory cache immediately, mark dirty, return without DB wait
- **Read path:** Check cache first, fallback to DB on miss
- **Flush:** Timer fires every 1 second, batch writes all dirty entries

This reduces DB writes by 40-50x during high throughput.

### PostgresAdminListener

Provides real-time admin dashboard updates via PostgreSQL NOTIFY.

**Location:** `src/Alberto.Dcb.Postgres/Admin/PostgresAdminListener.cs`

**Flow:**
1. LISTEN on three channels per module: `{schema}_events`, `{schema}_checkpoints`, `{schema}_dead_letters`
2. Debounce notifications (100ms window) to prevent duplicate queries
3. Batch process: query changed data, publish to GraphQL subscriptions
4. WebSocket pushes updates to connected admin clients

## Configuration

| Setting | Default | Purpose |
|---------|---------|---------|
| `PollingInterval` | 100ms | How often to check for new events |
| `BatchSize` | 100 | Events per poll cycle |
| `RebuildBatchSize` | 1000 | Events per rebuild cycle (faster catch-up) |
| `RebuildThreshold` | 1000 | Lag before processor enters rebuild mode |
| `MaxRetries` | 3 | Attempts before dead-lettering |
| `RetryDelay` | 1s | Wait between retries |
| `CheckpointFlushInterval` | 1s | Batch checkpoint writes |
| `DebounceInterval` | 100ms | Admin notification batching |

## Module Configuration Example

```csharp
services.AddAlberto("orders", builder => builder
    .WithPostgres(options =>
    {
        options.Schema = "orders";
        options.AutoMigrate = true;
    })
    .WithEntityFramework<OrdersDbContext>(options =>
    {
        options.UseNpgsql(connectionString);
    })
    .WithConsumer(consumer => consumer
        .WithPollingInterval(TimeSpan.FromMilliseconds(500))
        .WithBatchSize(100)
        .WithRebuildBatchSize(1000)
        .WithRebuildThreshold(1000)
        .WithErrorPolicy(policy =>
        {
            policy.MaxRetries(3);
            policy.RetryDelay(TimeSpan.FromSeconds(1));
            policy.DeadLetterOnMaxRetries(true);
        })
        .AddProjection<OrdersOverview, OrdersOverviewProjection>(...)
        .AddEfProjection<OrderSummaryEntity, OrderSummaryEfProjection, OrdersDbContext>()
    )
);
```

## Key Files

| Component | File |
|-----------|------|
| PollingConsumer | `src/Alberto.Dcb/Subscriptions/PollingConsumer.cs` |
| AsyncProjection | `src/Alberto.Dcb/Subscriptions/AsyncProjection.cs` |
| AsyncReactor | `src/Alberto.Dcb/Subscriptions/AsyncReactor.cs` |
| CachingCheckpointStore | `src/Alberto.Dcb/Subscriptions/CachingCheckpointStore.cs` |
| EfStateStore | `src/Alberto.Dcb.EntityFramework/EfStateStore.cs` |
| BatchedEfProjection | `src/Alberto.Dcb.EntityFramework/Batching/BatchedEfProjection.cs` |
| PostgresAdminListener | `src/Alberto.Dcb.Postgres/Admin/PostgresAdminListener.cs` |
| NOTIFY Triggers | `src/Alberto.Dcb.Postgres/Migrations/006_AdminNotifications.sql` |
