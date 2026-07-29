# Admin GraphQL Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract admin abstractions into `Alberto.Dcb.Admin`, add `PostgresAdminOperator` with transactional audit events, and expose the full admin surface via GraphQL (queries + mutations + subscriptions) using HotChocolate.

**Architecture:** Three-layer approach: `Alberto.Dcb.Admin` (pure abstractions — types, interfaces, events), `Alberto.Dcb.Postgres` (implements `IAdminReader` via existing `PostgresAdminDataAccess` + new `PostgresAdminOperator` for mutations with audit), and `Alberto.Dcb.Admin.GraphQL` (HotChocolate types registerable in any ASP.NET Core host). GraphQL subscriptions over WebSocket replace SignalR for real-time push.

**Tech Stack:** .NET 10.0, HotChocolate 15.1.15, Npgsql 10.0.2, xUnit v3 3.2.2

## Global Constraints

- `TreatWarningsAsErrors` is `true` on all projects
- All projects target `net9.0;net10.0`
- Central package versions in `Directory.Packages.props`
- HotChocolate uses annotation-based API (`[Query]`, `[Mutation]`, `[Subscription]` on static methods)
- Source-generated type discovery via `.AddTypes()` (`HotChocolate.Types.Analyzers`)
- Tenant ID propagated via `TenantHttpRequestInterceptor.TenantIdKey` global state
- Admin types currently live in `Alberto.Dcb.Postgres` namespace — they must move to `Alberto.Dcb.Admin`
- 14 CLI files reference `using Alberto.Dcb.Postgres` for these types — namespace must be updated
- `PostgresAdminDataAccess` is 824 lines — the record types at the top move to Admin; the class implements `IAdminReader`
- Rebuild store on main uses coordinator pattern (`RequestPromotionAsync`/`RequestAbortAsync`) — operator wraps these + appends audit event (not atomic for rebuilds, atomic for checkpoint/DLQ)
- `operatorId` defaults to `"admin-panel"` in GraphQL, `Environment.UserName` in CLI

---

### Task 1: Create `Alberto.Dcb.Admin` Library

**Files:**
- Create: `src/Alberto.Dcb.Admin/Alberto.Dcb.Admin.csproj`
- Create: `src/Alberto.Dcb.Admin/AdminTypes.cs`
- Create: `src/Alberto.Dcb.Admin/IAdminReader.cs`
- Create: `src/Alberto.Dcb.Admin/IAdminOperator.cs`
- Create: `src/Alberto.Dcb.Admin/AdminEvents.cs`
- Create: `src/Alberto.Dcb.Admin/AdminTags.cs`
- Modify: `AlbertoV3.slnx` — add project to `/src/` folder
- Modify: `src/Alberto.Dcb.Postgres/Alberto.Dcb.Postgres.csproj` — add project reference

**Interfaces:**
- Produces: `IAdminReader` (12 query methods), `IAdminOperator` (9 mutation methods), admin record types, admin event types, `AdminTags` constants

- [ ] **Step 1: Create the csproj**
- [ ] **Step 2: Create AdminTypes.cs** — extract record types from `PostgresAdminDataAccess.cs` (lines 12–96), keep main's versions which have `TenantId` fields
- [ ] **Step 3: Create IAdminReader.cs** — interface matching `PostgresAdminDataAccess`'s current public query methods
- [ ] **Step 4: Create IAdminOperator.cs** — mutation interface with `operatorId` parameter on all methods
- [ ] **Step 5: Create AdminEvents.cs** — event types implementing `IEvent` with `[EventType]`
- [ ] **Step 6: Create AdminTags.cs** — tag key constants
- [ ] **Step 7: Add to solution and Postgres csproj**
- [ ] **Step 8: Build and verify** — `dotnet build src/Alberto.Dcb.Admin/`

### Task 2: Refactor `PostgresAdminDataAccess` to Implement `IAdminReader`

**Files:**
- Modify: `src/Alberto.Dcb.Postgres/PostgresAdminDataAccess.cs` — remove record types, add `: IAdminReader`, use types from `Alberto.Dcb.Admin`
- Modify: 14 CLI files — update `using Alberto.Dcb.Postgres` → add `using Alberto.Dcb.Admin`
- Modify: `tools/Alberto.Cli/Alberto.Cli.csproj` — add project reference to Admin

**Interfaces:**
- Consumes: `IAdminReader`, admin record types from Task 1
- Produces: `PostgresAdminDataAccess : IAdminReader` (concrete implementation)

- [ ] **Step 1: Remove record types from `PostgresAdminDataAccess.cs`** — delete lines 6–96, add `using Alberto.Dcb.Admin;`
- [ ] **Step 2: Add `: IAdminReader` to class declaration**
- [ ] **Step 3: Fix any signature mismatches** — ensure methods match the interface exactly
- [ ] **Step 4: Update CLI imports** — add `using Alberto.Dcb.Admin;` to all 14 CLI files, add project reference
- [ ] **Step 5: Build solution** — `dotnet build`
- [ ] **Step 6: Run tests** — `dotnet test`

### Task 3: Add `PostgresAdminOperator`

**Files:**
- Create: `src/Alberto.Dcb.Postgres/PostgresAdminOperator.cs`
- Create: `tests/Alberto.Dcb.Tests/Postgres/PostgresAdminOperatorTests.cs`

**Interfaces:**
- Consumes: `IAdminOperator`, admin event types, `AdminTags` from Task 1
- Produces: `PostgresAdminOperator : IAdminOperator` (concrete implementation with transactional audit events)

- [ ] **Step 1: Create `PostgresAdminOperator.cs`** — checkpoint/DLQ mutations atomic with audit event; rebuild mutations call existing store + append audit separately
- [ ] **Step 2: Create tests** — verify checkpoint set/reset, DLQ clear/retry-rewind, and that audit events are appended
- [ ] **Step 3: Build and run tests** — `dotnet test --filter "FullyQualifiedName~PostgresAdminOperator"`

### Task 4: Create `Alberto.Dcb.Admin.GraphQL` Package

**Files:**
- Create: `src/Alberto.Dcb.Admin.GraphQL/Alberto.Dcb.Admin.GraphQL.csproj`
- Create: `src/Alberto.Dcb.Admin.GraphQL/AdminQueries.cs`
- Create: `src/Alberto.Dcb.Admin.GraphQL/AdminMutations.cs`
- Create: `src/Alberto.Dcb.Admin.GraphQL/AdminMutationResults.cs`
- Create: `src/Alberto.Dcb.Admin.GraphQL/AdminSubscriptions.cs`
- Create: `src/Alberto.Dcb.Admin.GraphQL/AdminSubscriptionTopics.cs`
- Create: `src/Alberto.Dcb.Admin.GraphQL/AdminGraphQLExtensions.cs`
- Modify: `AlbertoV3.slnx` — add project
- Modify: `Directory.Packages.props` — ensure HC packages listed

**Interfaces:**
- Consumes: `IAdminReader`, `IAdminOperator` from Tasks 1–3
- Produces: `AddAlbertoAdminGraphQL()` extension method, GraphQL types

- [ ] **Step 1: Create csproj** with references to Admin + HotChocolate
- [ ] **Step 2: Create AdminQueries.cs** — `[Query]` methods over `IAdminReader`
- [ ] **Step 3: Create AdminMutations.cs** — `[Mutation]` methods over `IAdminOperator`
- [ ] **Step 4: Create AdminMutationResults.cs** — result types for mutations
- [ ] **Step 5: Create AdminSubscriptions.cs** — `[Subscription]` methods with `[Subscribe]`/`[Topic]`
- [ ] **Step 6: Create AdminSubscriptionTopics.cs** — topic constants
- [ ] **Step 7: Create AdminGraphQLExtensions.cs** — `AddAlbertoAdminGraphQL()` registration
- [ ] **Step 8: Add to solution, build** — `dotnet build src/Alberto.Dcb.Admin.GraphQL/`

### Task 5: Wire Into Orders API + Aspire

**Files:**
- Modify: `apps/Alberto.Orders/Alberto.Orders.Api/Program.cs` — add admin GraphQL types + DI
- Modify: `apps/Alberto.Orders/Alberto.Orders.Api/Alberto.Orders.Api.csproj` — add references

- [ ] **Step 1: Add project references** to Admin, Admin.GraphQL, and Postgres
- [ ] **Step 2: Register DI services** — `IAdminReader`, `IAdminOperator` from Npgsql data source
- [ ] **Step 3: Add admin types to GraphQL server** — `.AddAlbertoAdminGraphQL()`
- [ ] **Step 4: Full build** — `dotnet build`
- [ ] **Step 5: Start Aspire and smoke test** — `dotnet run --project apps/Alberto.AppHost`, verify admin queries appear in Banana Cake Pop

### Task 6: Full Verification

- [ ] **Step 1: Full test suite** — `dotnet test`
- [ ] **Step 2: Verify GraphQL schema** — check queries, mutations, subscriptions show up
- [ ] **Step 3: Commit** — feature branch + commit
