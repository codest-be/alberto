# Vertical Slices With State Per Slice — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the Orders and Payments examples to true vertical slices, where every write and read operation owns its own state record, evolver, decision function and GraphQL operation in one file, and the only thing shared between slices is the event log.

**Architecture:** Each module collapses from three layer-projects (`.Core`, `.Infrastructure`, and its share of `.Api`) into one class library with three top-level folders: `Contracts/` (events, status enum, problems, tags), `Features/` (one folder per slice), `Platform/` (DI registration, EF `DbContext`, EF migrations). The API project shrinks to `Program.cs` plus the tenant interceptor. Nothing in `src/Alberto.Dcb*` changes: `CommandPipeline.Load<TState>(boundary, evolver)` already takes the evolver per call site, DI resolves on the closed generic `Evolver<TState>`, and `EvolverDispatcher` ignores event types a slice evolver does not handle.

**Tech Stack:** .NET 10, HotChocolate 15.1.15 (source-generated `[Query]`/`[Mutation]` via `HotChocolate.Types.Analyzers`), EF Core 10, Npgsql, xUnit v3 + FluentAssertions.

## Global Constraints

- **The GraphQL schema must not change.** Same operation names, argument names, input type names, result shapes, enum values and `[GraphQLDescription]` text. K6 load tests in `tests/Alberto.Orders.LoadTests` pin them. Task 1 builds a snapshot test that enforces this; it must pass at the end of **every** task.
- **No change to `src/Alberto.Dcb*`.** If a task appears to need one, stop and report it rather than making it.
- **No narrowing of DCB boundaries.** Every slice keeps `DcbQuery.For(Tags.Order, orderId)` / `DcbQuery.For(Tags.Payment, paymentId)`.
- **No change to event types, tag keys, or stored data.** The refactor is invisible to an existing database.
- **Error messages are contract.** `OrderProblems.InvalidStatus(...)` and `PaymentProblems.InvalidStatus(...)` embed `state.Status`, so every slice state must fold **every** status-changing event even when its own guard would refuse without them — otherwise a rejection reports the wrong status. (This extends the four-event `ShipOrderEvolver` sketched in the spec to five; see Task 10.)
- **Nothing outside a slice folder may reference anything inside it.** `Contracts/` and `Platform/` are the two deliberate exceptions.
- **No shared state helpers.** No base state record, no shared `ApplyCreated`, no `I*State` interfaces. Duplication between slice evolvers is the pattern working.
- Target framework `net10.0`, `ImplicitUsings` and `Nullable` enabled on every new project. Package versions come from `Directory.Packages.props` — add a `<PackageVersion>` there if a package is new to the repo, and reference it without a `Version` attribute.

---

## File Structure

**New projects:**

| Path | Responsibility |
|---|---|
| `apps/Alberto.Examples.Shared/Alberto.Examples.Shared.csproj` | `MutationResult`, `MutationError`, `EnsureCommitted`. Shared because `MutationResult` is one GraphQL type used by both modules' mutations; two CLR types of that name would collide in the schema. |
| `apps/Alberto.Orders/Alberto.Orders/Alberto.Orders.csproj` | The Orders module: contracts, all slices, platform wiring. |
| `apps/Alberto.Payments/Alberto.Payments/Alberto.Payments.csproj` | The Payments module, same shape. |
| `tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj` | Schema snapshot test plus one decision test per slice. |

**Deleted projects** (after their contents move): `Alberto.Orders.Core`, `Alberto.Orders.Infrastructure`, `Alberto.Payments.Core`, `Alberto.Payments.Infrastructure`.

**Orders module layout:**

```
apps/Alberto.Orders/Alberto.Orders/
  Contracts/OrderEvents.cs        OrderCreated … OrderCancelled, OrderLineItem
  Contracts/OrderStatus.cs        the enum only
  Contracts/OrderProblems.cs      unchanged
  Contracts/Tags.cs               unchanged
  Contracts/Order.cs              GraphQL output types: Order, OrderItem, OrdersConnection
  Features/CreateOrder/CreateOrder.cs
  Features/AddOrderItem/AddOrderItem.cs
  Features/RemoveOrderItem/RemoveOrderItem.cs
  Features/ConfirmOrder/ConfirmOrder.cs
  Features/ShipOrder/ShipOrder.cs
  Features/DeliverOrder/DeliverOrder.cs
  Features/CancelOrder/CancelOrder.cs
  Features/GetOrder/GetOrder.cs
  Features/OrderSummaries/OrderSummaries.cs      EF projection + getOrders + recentOrders
  Features/OrdersOverview/OrdersOverview.cs      projection + read model + ordersOverview
  Platform/OrdersModule.cs
  Platform/Data/OrdersDbContext.cs, OrdersDbContextFactory.cs
  Platform/Entities/OrderSummaryEntity.cs, OrderLineItemEntity.cs
  Platform/Migrations/*.cs        EF migrations, ids untouched
  Properties/ModuleInfo.cs        [assembly: Module("OrdersTypes")]
```

**Payments module layout:** identical shape, with `Features/{InitiatePayment,AuthorizePayment,CapturePayment,FailPayment,RefundPayment,GetPayment,PaymentSummaries,PaymentsOverview}` and no EF folder.

**Naming convention inside a slice file** (one file, four public types):

- `ShipOrderInput` — the GraphQL input record (only where one exists today)
- `ShipOrderState` — the slice's state record
- `ShipOrderEvolver` — `Evolver<ShipOrderState>` folding only the events the decision needs
- `ShipOrderDecider` — `static`, holds `Boundary(...)` and `Decide(...)`
- `ShipOrderMutation` / `GetOrderQuery` — `static`, holds the `[Mutation]`/`[Query]` method

The decider and the mutation are separate classes because C# forbids a member with the same name as its enclosing type, and the mutation method name is fixed by the schema.

---

## Task 1: Schema snapshot harness

The refactor's one hard invariant is "the schema does not change". Build the gate before touching anything, so every later task can prove it.

**Files:**
- Create: `tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj`
- Create: `tests/Alberto.Examples.Tests/SchemaSnapshotTests.cs`
- Create: `tests/Alberto.Examples.Tests/Snapshots/schema.graphql` (generated in step 4)
- Modify: `AlbertoV3.slnx`

**Interfaces:**
- Produces: `SchemaSnapshotTests.BuildSchemaAsync()` — the single place that lists the GraphQL type modules. Later tasks change the `AddTypes()` call inside it exactly once (Task 5) and never again.

- [ ] **Step 1: Create the test project**

`tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\apps\Alberto.Orders\Alberto.Orders.Api\Alberto.Orders.Api.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
  </ItemGroup>

  <ItemGroup>
    <None Update="Snapshots\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

Referencing the Api project (a web project) is deliberate and temporary: it is where the GraphQL types live today. Task 5 replaces this reference with the two module libraries.

- [ ] **Step 2: Write the snapshot test**

`tests/Alberto.Examples.Tests/SchemaSnapshotTests.cs`:

```csharp
using Alberto.Dcb.Admin.GraphQL;
using FluentAssertions;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Examples.Tests;

/// <summary>
/// Pins the served GraphQL schema. The K6 load tests in tests/Alberto.Orders.LoadTests send
/// operations by name against a running API, so a renamed field or input type is a broken
/// client, not a refactor. This builds the schema without a database — schema construction
/// inspects types, it does not resolve the services the resolvers ask for.
/// </summary>
public sealed class SchemaSnapshotTests
{
    private static readonly string SnapshotPath =
        Path.Combine(AppContext.BaseDirectory, "Snapshots", "schema.graphql");

    [Fact]
    public async Task Schema_matches_snapshot()
    {
        var actual = await PrintSchemaAsync();
        var expected = await File.ReadAllTextAsync(SnapshotPath);

        actual.Should().Be(
            expected,
            "the GraphQL schema is a published contract; run SchemaSnapshotTests.Rewrite to " +
            "update the snapshot only when a schema change is intended");
    }

    /// <summary>
    /// The single place that lists the GraphQL type modules. Kept in one method so the wiring
    /// change in Task 5 happens once.
    /// </summary>
    public static async Task<string> PrintSchemaAsync()
    {
        var schema = await new ServiceCollection()
            .AddGraphQLServer()
            .AddTypes()
            .AddAlbertoAdminGraphQL()
            .BuildSchemaAsync();

        return schema.ToString();
    }
}
```

- [ ] **Step 3: Register the project and confirm it fails without a snapshot**

Add to `AlbertoV3.slnx`, inside the `/tests/` folder element:

```xml
    <Project Path="tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj" />
```

Run:

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~Schema_matches_snapshot"
```

Expected: FAIL with `FileNotFoundException` for `Snapshots/schema.graphql`.

- [ ] **Step 4: Generate the snapshot from the current code**

Write `tests/Alberto.Examples.Tests/RewriteSnapshot.cs`:

```csharp
using FluentAssertions;

namespace Alberto.Examples.Tests;

/// <summary>
/// Not a check — a generator. Run it deliberately to re-record the snapshot after an
/// *intended* schema change, then read the diff before committing it.
/// </summary>
public sealed class RewriteSnapshot
{
    [Fact(Explicit = true)]
    public async Task Rewrite()
    {
        var sdl = await SchemaSnapshotTests.PrintSchemaAsync();
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var target = Path.Combine(
            repoRoot, "tests/Alberto.Examples.Tests/Snapshots/schema.graphql");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, sdl);

        File.Exists(target).Should().BeTrue();
    }
}
```

Run:

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~RewriteSnapshot" -- xUnit.ExplicitTests=on
```

Expected: PASS, and `tests/Alberto.Examples.Tests/Snapshots/schema.graphql` now exists containing `type Query`, `type Mutation`, `input CreateOrderInput`, `input ShipOrderInput` and the rest.

- [ ] **Step 5: Verify the gate passes**

```bash
dotnet test tests/Alberto.Examples.Tests
```

Expected: PASS (the explicit rewrite test is skipped).

- [ ] **Step 6: Commit**

```bash
git add tests/Alberto.Examples.Tests AlbertoV3.slnx && git commit -m "test(examples): pin the GraphQL schema with a snapshot test"
```

---

## Task 2: Shared mutation-result contracts project

`MutationResult` is returned by mutations in both modules and appears once in the schema. Two CLR types with that name would be a schema conflict, so it moves to a project both modules reference. `EnsureCommitted` goes with it and becomes `public`.

**Files:**
- Create: `apps/Alberto.Examples.Shared/Alberto.Examples.Shared.csproj`
- Create: `apps/Alberto.Examples.Shared/MutationResults.cs`
- Modify: `AlbertoV3.slnx`
- Delete (in Task 5, once nothing references them): the equivalents in `Alberto.Orders.Api`

**Interfaces:**
- Produces: namespace `Alberto.Examples.Shared`, containing
  - `public readonly record struct MutationResult { public bool Success => true; }`
  - `public sealed record MutationError(string Message)`
  - `public static void EnsureCommitted(this Result result)`
  - `public static T EnsureCommitted<T>(this Alberto.Dcb.Result<T> result)`

- [ ] **Step 1: Create the project**

`apps/Alberto.Examples.Shared/Alberto.Examples.Shared.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Alberto.Dcb\Alberto.Dcb.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="HotChocolate.Types" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write the file**

`apps/Alberto.Examples.Shared/MutationResults.cs`:

```csharp
using Alberto.Dcb;

namespace Alberto.Examples.Shared;

/// <summary>
/// Result of a mutation that doesn't return data.
/// </summary>
/// <remarks>
/// Shared by both example modules rather than duplicated per module: this is one GraphQL type,
/// and two CLR types named MutationResult would collide when the schema is built. It is
/// transport, not domain state — the rule about slices not sharing state is about what a
/// decision reads, not about the envelope the answer travels in.
/// </remarks>
public readonly record struct MutationResult
{
    public bool Success => true;
}

/// <summary>
/// Error result for failed mutations.
/// </summary>
public sealed record MutationError(string Message);

/// <summary>
/// Turns a failed <see cref="Result"/> into a GraphQL error, preserving the
/// <see cref="Problem.Code"/> so clients branch on the code rather than the message.
/// </summary>
public static class MutationResults
{
    public static void EnsureCommitted(this Result result)
    {
        if (result.IsFailure)
            throw ToException(result.Problems);
    }

    // Fully qualified: HotChocolate's global usings pull in GreenDonut.Result<T>.
    public static T EnsureCommitted<T>(this Alberto.Dcb.Result<T> result) =>
        result.IsSuccess ? result.Value : throw ToException(result.Problems);

    private static GraphQLException ToException(IReadOnlyList<Problem> problems) =>
        new(problems
            .Select(problem => ErrorBuilder.New()
                .SetMessage(problem.Message)
                .SetCode(problem.Code)
                .Build())
            .ToArray());
}
```

- [ ] **Step 3: Register and build**

Add to `AlbertoV3.slnx` inside the `/apps/` folder element:

```xml
    <Project Path="apps/Alberto.Examples.Shared/Alberto.Examples.Shared.csproj" />
```

Run:

```bash
dotnet build apps/Alberto.Examples.Shared
```

Expected: build succeeds. Nothing references it yet.

- [ ] **Step 4: Commit**

```bash
git add apps/Alberto.Examples.Shared AlbertoV3.slnx && git commit -m "feat(examples): shared mutation-result contracts project"
```

---

## Task 3: Merge Orders Core + Infrastructure into one module library

A pure move: same code, same namespaces reorganised, no slicing yet. The schema snapshot must still pass at the end.

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Alberto.Orders.csproj`
- Move: every `.cs` file from `Alberto.Orders.Core` and `Alberto.Orders.Infrastructure` (paths in step 2)
- Delete: `apps/Alberto.Orders/Alberto.Orders.Core/`, `apps/Alberto.Orders/Alberto.Orders.Infrastructure/`
- Modify: `AlbertoV3.slnx`, `apps/Alberto.Orders/Alberto.Orders.Api/Alberto.Orders.Api.csproj`, `apps/Alberto.Orders/Alberto.Orders.Migrations/Alberto.Orders.Migrations.csproj`, and every file whose `using` changes

**Interfaces:**
- Produces: assembly `Alberto.Orders`, namespaces `Alberto.Orders.Contracts` (events, `OrderStatus`, `OrderProblems`, `Tags`), `Alberto.Orders.Platform` (`OrdersModule`, `OrdersDbContext`, entities), `Alberto.Orders.Platform.Migrations` (EF migrations), `Alberto.Orders.Features` (deciders and projections, until Tasks 6–20 restructure them).
- Consumes: `Alberto.Examples.Shared` from Task 2 (referenced here, used from Task 5).

- [ ] **Step 1: Create the module project**

`apps/Alberto.Orders/Alberto.Orders/Alberto.Orders.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Alberto.Examples.Shared\Alberto.Examples.Shared.csproj" />
    <ProjectReference Include="..\..\..\src\Alberto.Dcb\Alberto.Dcb.csproj" />
    <ProjectReference Include="..\..\..\src\Alberto.Dcb.Commands\Alberto.Dcb.Commands.csproj" />
    <ProjectReference Include="..\..\..\src\Alberto.Dcb.Postgres\Alberto.Dcb.Postgres.csproj" />
    <ProjectReference Include="..\..\..\src\Alberto.Dcb.Telemetry\Alberto.Dcb.Telemetry.csproj" />
    <ProjectReference Include="..\..\..\src\Alberto.Dcb.EntityFramework\Alberto.Dcb.EntityFramework.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="HotChocolate.Types" />
    <PackageReference Include="HotChocolate.Types.Analyzers" />
  </ItemGroup>

</Project>
```

`HotChocolate.Types` and the analyzer are here from the start so Task 5 can move `[Mutation]`/`[Query]` methods in without touching the project file again.

- [ ] **Step 2: Move the files with `git mv`**

```bash
cd apps/Alberto.Orders
mkdir -p Alberto.Orders/Contracts Alberto.Orders/Features Alberto.Orders/Platform/Data Alberto.Orders/Platform/Entities Alberto.Orders/Platform/Migrations Alberto.Orders/Properties
git mv Alberto.Orders.Core/Order/OrderEvents.cs      Alberto.Orders/Contracts/OrderEvents.cs
git mv Alberto.Orders.Core/Order/OrderProblems.cs    Alberto.Orders/Contracts/OrderProblems.cs
git mv Alberto.Orders.Core/Tags.cs                   Alberto.Orders/Contracts/Tags.cs
git mv Alberto.Orders.Core/Order/OrderState.cs       Alberto.Orders/Features/OrderState.cs
git mv Alberto.Orders.Core/Order/OrderEvolver.cs     Alberto.Orders/Features/OrderEvolver.cs
git mv Alberto.Orders.Core/Order/OrderDecider.cs     Alberto.Orders/Features/OrderDecider.cs
git mv Alberto.Orders.Core/Order/Actions            Alberto.Orders/Features/Actions
git mv Alberto.Orders.Infrastructure/OrdersModule.cs Alberto.Orders/Platform/OrdersModule.cs
git mv Alberto.Orders.Infrastructure/Data/OrdersDbContext.cs        Alberto.Orders/Platform/Data/OrdersDbContext.cs
git mv Alberto.Orders.Infrastructure/Data/OrdersDbContextFactory.cs Alberto.Orders/Platform/Data/OrdersDbContextFactory.cs
git mv Alberto.Orders.Infrastructure/Entities/OrderSummaryEntity.cs  Alberto.Orders/Platform/Entities/OrderSummaryEntity.cs
git mv Alberto.Orders.Infrastructure/Entities/OrderLineItemEntity.cs Alberto.Orders/Platform/Entities/OrderLineItemEntity.cs
git mv Alberto.Orders.Infrastructure/Projections  Alberto.Orders/Features/Projections
git mv Alberto.Orders.Infrastructure/ReadModels   Alberto.Orders/Features/ReadModels
for f in Alberto.Orders.Infrastructure/Migrations/*; do git mv "$f" Alberto.Orders/Platform/Migrations/; done
```

`OrderState.cs`, `OrderEvolver.cs`, `OrderDecider.cs` and `Features/Actions/` land in `Features/` unchanged and are deleted slice-by-slice in Tasks 6–12; `Features/Projections` and `Features/ReadModels` are folded into read slices in Tasks 19–20.

- [ ] **Step 3: Rewrite namespaces**

Apply these namespace renames across the whole repository (all `.cs` files, `namespace` declarations and `using` directives alike):

| Old | New |
|---|---|
| `Alberto.Orders.Core.Order` | `Alberto.Orders.Contracts` |
| `Alberto.Orders.Core.Order.Actions` | `Alberto.Orders.Features` |
| `Alberto.Orders.Core` | `Alberto.Orders.Contracts` |
| `Alberto.Orders.Infrastructure.Data` | `Alberto.Orders.Platform.Data` |
| `Alberto.Orders.Infrastructure.Entities` | `Alberto.Orders.Platform.Entities` |
| `Alberto.Orders.Infrastructure.Migrations` | `Alberto.Orders.Platform.Migrations` |
| `Alberto.Orders.Infrastructure.Projections` | `Alberto.Orders.Features.Projections` |
| `Alberto.Orders.Infrastructure.ReadModels` | `Alberto.Orders.Features.ReadModels` |
| `Alberto.Orders.Infrastructure` | `Alberto.Orders.Platform` |

Order matters — apply the longest prefixes first, or the last row will eat the ones above it. The EF migration files keep their `[Migration("2026...")]` ids exactly as they are; only their namespace changes.

- [ ] **Step 4: Repoint project references**

In `apps/Alberto.Orders/Alberto.Orders.Api/Alberto.Orders.Api.csproj` and `apps/Alberto.Orders/Alberto.Orders.Migrations/Alberto.Orders.Migrations.csproj`, replace

```xml
<ProjectReference Include="..\Alberto.Orders.Infrastructure\Alberto.Orders.Infrastructure.csproj" />
```

with

```xml
<ProjectReference Include="..\Alberto.Orders\Alberto.Orders.csproj" />
```

In `AlbertoV3.slnx`, replace the two `Alberto.Orders.Core` / `Alberto.Orders.Infrastructure` project elements with:

```xml
    <Project Path="apps/Alberto.Orders/Alberto.Orders/Alberto.Orders.csproj" />
```

Then delete the empty project directories:

```bash
git rm -r apps/Alberto.Orders/Alberto.Orders.Core apps/Alberto.Orders/Alberto.Orders.Infrastructure
```

- [ ] **Step 5: Check the EF migrations assembly**

```bash
grep -rn "MigrationsAssembly" apps/ src/
```

Expected: no hits. If there is one naming `Alberto.Orders.Infrastructure`, change it to `Alberto.Orders` — the migrations moved with the `DbContext`, so the default (the context's own assembly) is correct.

- [ ] **Step 6: Build and verify the schema is unchanged**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: build succeeds, snapshot test PASSES. A failure here means a GraphQL type moved namespace in a way HotChocolate noticed — investigate rather than re-recording the snapshot.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "refactor(orders): merge Core and Infrastructure into one module library"
```

---

## Task 4: Merge Payments Core + Infrastructure into one module library

Same move for Payments. It has no EF context and no host of its own.

**Files:**
- Create: `apps/Alberto.Payments/Alberto.Payments/Alberto.Payments.csproj`
- Move: every `.cs` file from `Alberto.Payments.Core` and `Alberto.Payments.Infrastructure`
- Delete: `apps/Alberto.Payments/Alberto.Payments.Core/`, `apps/Alberto.Payments/Alberto.Payments.Infrastructure/`
- Modify: `AlbertoV3.slnx`, `apps/Alberto.Orders/Alberto.Orders.Api/Alberto.Orders.Api.csproj`

**Interfaces:**
- Produces: assembly `Alberto.Payments`, namespaces `Alberto.Payments.Contracts` (events, `PaymentStatus`, `PaymentProblems`, `Tags`), `Alberto.Payments.Platform` (`PaymentsModule`), `Alberto.Payments.Features`.

- [ ] **Step 1: Create the module project**

`apps/Alberto.Payments/Alberto.Payments/Alberto.Payments.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Alberto.Examples.Shared\Alberto.Examples.Shared.csproj" />
    <ProjectReference Include="..\..\..\src\Alberto.Dcb\Alberto.Dcb.csproj" />
    <ProjectReference Include="..\..\..\src\Alberto.Dcb.Commands\Alberto.Dcb.Commands.csproj" />
    <ProjectReference Include="..\..\..\src\Alberto.Dcb.Postgres\Alberto.Dcb.Postgres.csproj" />
    <ProjectReference Include="..\..\..\src\Alberto.Dcb.Telemetry\Alberto.Dcb.Telemetry.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="HotChocolate.Types" />
    <PackageReference Include="HotChocolate.Types.Analyzers" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Move the files**

```bash
cd apps/Alberto.Payments
mkdir -p Alberto.Payments/Contracts Alberto.Payments/Features Alberto.Payments/Platform Alberto.Payments/Properties
git mv Alberto.Payments.Core/Events/PaymentEvents.cs       Alberto.Payments/Contracts/PaymentEvents.cs
git mv Alberto.Payments.Core/Payment/PaymentProblems.cs    Alberto.Payments/Contracts/PaymentProblems.cs
git mv Alberto.Payments.Core/Tags.cs                       Alberto.Payments/Contracts/Tags.cs
git mv Alberto.Payments.Core/Payment/PaymentState.cs       Alberto.Payments/Features/PaymentState.cs
git mv Alberto.Payments.Core/Payment/PaymentEvolver.cs     Alberto.Payments/Features/PaymentEvolver.cs
git mv Alberto.Payments.Core/Payment/PaymentDecider.cs     Alberto.Payments/Features/PaymentDecider.cs
git mv Alberto.Payments.Core/Payment/Actions               Alberto.Payments/Features/Actions
git mv Alberto.Payments.Infrastructure/PaymentsModule.cs   Alberto.Payments/Platform/PaymentsModule.cs
git mv Alberto.Payments.Infrastructure/Projections         Alberto.Payments/Features/Projections
git mv Alberto.Payments.Infrastructure/ReadModels          Alberto.Payments/Features/ReadModels
```

- [ ] **Step 3: Rewrite namespaces**

Longest prefixes first, repository-wide:

| Old | New |
|---|---|
| `Alberto.Payments.Core.Payment.Actions` | `Alberto.Payments.Features` |
| `Alberto.Payments.Core.Payment` | `Alberto.Payments.Contracts` |
| `Alberto.Payments.Core.Events` | `Alberto.Payments.Contracts` |
| `Alberto.Payments.Core` | `Alberto.Payments.Contracts` |
| `Alberto.Payments.Infrastructure.Projections` | `Alberto.Payments.Features.Projections` |
| `Alberto.Payments.Infrastructure.ReadModels` | `Alberto.Payments.Features.ReadModels` |
| `Alberto.Payments.Infrastructure` | `Alberto.Payments.Platform` |

`PaymentsModule.cs` references `typeof(Core.Events.PaymentInitiated).Assembly` — change it to `typeof(Contracts.PaymentInitiated).Assembly`.

Watch `PaymentTypes.cs`: it aliases `CorePaymentStatus = Alberto.Payments.Core.Payment.PaymentStatus` and also uses the read model's own `PaymentStatus`. After the rename the alias becomes `Alberto.Payments.Contracts.PaymentStatus`; the read-model enum in `Alberto.Payments.Features.ReadModels` keeps its name and the explicit `ToCoreStatus` mapping stays exactly as it is.

- [ ] **Step 4: Repoint references and delete the old projects**

In `apps/Alberto.Orders/Alberto.Orders.Api/Alberto.Orders.Api.csproj`, replace the `Alberto.Payments.Infrastructure` reference with:

```xml
<ProjectReference Include="..\..\Alberto.Payments\Alberto.Payments\Alberto.Payments.csproj" />
```

In `AlbertoV3.slnx`, replace the two `Alberto.Payments.*` project elements with:

```xml
    <Project Path="apps/Alberto.Payments/Alberto.Payments/Alberto.Payments.csproj" />
```

```bash
git rm -r apps/Alberto.Payments/Alberto.Payments.Core apps/Alberto.Payments/Alberto.Payments.Infrastructure
```

- [ ] **Step 5: Build and verify**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: build succeeds, snapshot test PASSES.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(payments): merge Core and Infrastructure into one module library"
```

---

## Task 5: Move the GraphQL surface into the modules

The mutations and queries move into the module assemblies whole — still one file per module per direction. Slicing them is Tasks 6–23. After this task the Api project contains only `Program.cs` and the tenant interceptor.

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Properties/ModuleInfo.cs`, `apps/Alberto.Payments/Alberto.Payments/Properties/ModuleInfo.cs`
- Create: `apps/Alberto.Orders/Alberto.Orders/Contracts/Order.cs`, `apps/Alberto.Payments/Alberto.Payments/Contracts/Payment.cs`
- Move: the four GraphQL files out of `Alberto.Orders.Api/GraphQL/`
- Delete: `apps/Alberto.Orders/Alberto.Orders.Api/GraphQL/MutationResults.cs`, `.../Types/OrderTypes.cs`, `.../Types/PaymentTypes.cs`, `.../Properties/ModuleInfo.cs`
- Modify: `apps/Alberto.Orders/Alberto.Orders.Api/Program.cs`, `apps/Alberto.Orders/Alberto.Orders.Api/Alberto.Orders.Api.csproj`, `tests/Alberto.Examples.Tests/*`

**Interfaces:**
- Produces: `AddOrdersTypes()` and `AddPaymentsTypes()` extension methods on `IRequestExecutorBuilder`, generated by `HotChocolate.Types.Analyzers` from the `[assembly: Module(...)]` attributes.
- Produces: `Alberto.Orders.Contracts.Order`, `.OrderItem`, `.OrdersConnection`; `Alberto.Payments.Contracts.Payment`.
- Produces: read slices obtain the tenant from `ITenantAccessor.TenantId` (scoped, registered by `AddTenancy()`), replacing `IResolverContext.GetGlobalState<string>(TenantHttpRequestInterceptor.TenantIdKey)`. The interceptor already calls `tenantContext.SetTenant(tenantId)`, so both sources carry the same value — this one does not require module code to know a host type.

- [ ] **Step 1: Declare a type module per assembly**

`apps/Alberto.Orders/Alberto.Orders/Properties/ModuleInfo.cs`:

```csharp
[assembly: Module("OrdersTypes")]
```

`apps/Alberto.Payments/Alberto.Payments/Properties/ModuleInfo.cs`:

```csharp
[assembly: Module("PaymentsTypes")]
```

Delete `apps/Alberto.Orders/Alberto.Orders.Api/Properties/ModuleInfo.cs` — its `DataLoaderModule`/`DataLoaderDefaults` attributes configure a generator for DataLoaders the example does not have.

- [ ] **Step 2: Split the shared output types into module contracts**

Create `apps/Alberto.Orders/Alberto.Orders/Contracts/Order.cs` holding the `Order`, `OrderItem` and `OrdersConnection` records currently in `Alberto.Orders.Api/GraphQL/Types/OrderTypes.cs:9-73`, in namespace `Alberto.Orders.Contracts`, with `using Alberto.Orders.Platform.Entities;` for the `FromEntity` factories.

Create `apps/Alberto.Payments/Alberto.Payments/Contracts/Payment.cs` holding the `Payment` record from `Alberto.Orders.Api/GraphQL/Types/PaymentTypes.cs:9-67` — including `FromSummary` and the `ToCoreStatus` mapping and its remarks block verbatim — in namespace `Alberto.Payments.Contracts`, with `using Alberto.Payments.Features.ReadModels;` and the alias `using CorePaymentStatus = Alberto.Payments.Contracts.PaymentStatus;` removed in favour of naming `PaymentStatus` directly and aliasing the read model's enum instead:

```csharp
using ReadModelStatus = Alberto.Payments.Features.ReadModels.PaymentStatus;
```

Update `ToCoreStatus`'s parameter type to `ReadModelStatus` and its cases to `ReadModelStatus.Initiated` etc. The GraphQL enum is unchanged: it is still the domain `PaymentStatus`.

- [ ] **Step 3: Move the input and result records to their modules**

Move the remaining records from `OrderTypes.cs` into `apps/Alberto.Orders/Alberto.Orders/Contracts/OrderInputs.cs` (namespace `Alberto.Orders.Contracts`): `CreateOrderInput`, `OrderItemInput`, `AddOrderItemInput`, `ShipOrderInput`, `CancelOrderInput`, `CreateOrderResult`. Tasks 6–12 move each into its slice file; this is a staging step so the build stays green.

Move the remaining records from `PaymentTypes.cs` into `apps/Alberto.Payments/Alberto.Payments/Contracts/PaymentInputs.cs` (namespace `Alberto.Payments.Contracts`): `InitiatePaymentInput`, `CapturePaymentInput`, `FailPaymentInput`, `RefundPaymentInput`, `InitiatePaymentResult`.

Delete `MutationResult` and `MutationError` from the moved code — they now come from `Alberto.Examples.Shared` (Task 2). Delete `apps/Alberto.Orders/Alberto.Orders.Api/GraphQL/MutationResults.cs`.

- [ ] **Step 4: Move the mutations and queries**

```bash
cd apps/Alberto.Orders
git mv Alberto.Orders.Api/GraphQL/Mutations/OrderMutations.cs Alberto.Orders/Features/OrderMutations.cs
git mv Alberto.Orders.Api/GraphQL/Queries/OrderQueries.cs     Alberto.Orders/Features/OrderQueries.cs
git mv Alberto.Orders.Api/GraphQL/Mutations/PaymentMutations.cs ../Alberto.Payments/Alberto.Payments/Features/PaymentMutations.cs
git mv Alberto.Orders.Api/GraphQL/Queries/PaymentQueries.cs     ../Alberto.Payments/Alberto.Payments/Features/PaymentQueries.cs
git rm Alberto.Orders.Api/GraphQL/Types/OrderTypes.cs Alberto.Orders.Api/GraphQL/Types/PaymentTypes.cs
```

In all four moved files: set the namespace to `Alberto.Orders.Features` / `Alberto.Payments.Features`, replace `using Alberto.Orders.Api.GraphQL.Types;` with the module's `Contracts` namespace, and add `using Alberto.Examples.Shared;`.

Replace the tenant lookup in both query files:

```csharp
    private static string GetTenantId(IResolverContext context) =>
        context.GetGlobalState<string>(TenantHttpRequestInterceptor.TenantIdKey)
        ?? throw new InvalidOperationException("Tenant ID not found in resolver context");
```

with a resolver parameter. Add `[Service] ITenantAccessor tenantAccessor` to `GetOrders`, `GetRecentOrders` and `GetRecentPayments`, use `tenantAccessor.TenantId`, and delete the helper and the now-unused `IResolverContext context` parameters. `ITenantAccessor` is scoped and registered by `AddTenancy()`; `TenantHttpRequestInterceptor` already calls `TenantContext.SetTenant`, which is what backs it.

`IResolverContext` is not a GraphQL argument, so removing it does not change the schema — the snapshot test in step 7 proves that.

- [ ] **Step 5: Wire the modules into the schema**

In `apps/Alberto.Orders/Alberto.Orders.Api/Program.cs`, replace `.AddTypes()` (line 49) with:

```csharp
    .AddOrdersTypes()
    .AddPaymentsTypes()
```

Update the `using` block: `Alberto.Orders.Infrastructure` → `Alberto.Orders.Platform`, `Alberto.Payments.Infrastructure` → `Alberto.Payments.Platform` (already done in Tasks 3–4).

Remove `HotChocolate.Types.Analyzers` from `Alberto.Orders.Api.csproj` — the Api no longer declares GraphQL types. Keep `HotChocolate.AspNetCore` and `HotChocolate.Subscriptions.InMemory`.

- [ ] **Step 6: Point the snapshot test at the modules**

In `tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj`, replace the Api project reference with:

```xml
    <ProjectReference Include="..\..\apps\Alberto.Orders\Alberto.Orders\Alberto.Orders.csproj" />
    <ProjectReference Include="..\..\apps\Alberto.Payments\Alberto.Payments\Alberto.Payments.csproj" />
    <ProjectReference Include="..\..\src\Alberto.Dcb.Admin.GraphQL\Alberto.Dcb.Admin.GraphQL.csproj" />
```

In `SchemaSnapshotTests.PrintSchemaAsync`, replace `.AddTypes()` with `.AddOrdersTypes().AddPaymentsTypes()`.

- [ ] **Step 7: Build and verify the schema is byte-identical**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: build succeeds, snapshot test PASSES. This is the step most likely to surface an accidental schema change — a moved input type that HotChocolate now names differently, or a resolver parameter that stopped being a service. Fix the code; do not re-record the snapshot.

- [ ] **Step 8: Confirm the Api is now a host only**

```bash
find apps/Alberto.Orders/Alberto.Orders.Api -name '*.cs' -not -path '*/obj/*'
```

Expected exactly: `Program.cs` and `GraphQL/TenantHttpRequestInterceptor.cs`.

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "refactor(examples): modules own their GraphQL surface"
```

---
## Tasks 6–17: the write slices

Tasks 6–17 all follow the same shape, and each one spells out its own code. Two rules that apply to every one of them:

**Fold every status-changing event.** A guard like `CanBeShipped` only needs to know whether the status is `Confirmed`, but the refusal message is `OrderProblems.InvalidStatus("shipped", state.Status)` — it names the status. A slice that skips `OrderDelivered` would tell a client that a delivered order "cannot be shipped in Shipped status". So each slice folds all five order-status events (`OrderCreated`, `OrderConfirmed`, `OrderShipped`, `OrderDelivered`, `OrderCancelled`) or all five payment-status events, and differs in the *data* it carries alongside the status. `CreateOrder` is the exception: it decides on emptiness alone.

**Leave the old `Apply` fragments alone.** Deleting `Features/Actions/Ship.cs` outright would remove `OrderDecider.Apply(OrderState, OrderShipped)`, which `OrderEvolver` still needs for the slices not yet converted and for the `GetOrder` query. Each task deletes only the `I*State` interface and the `static Decision ...` method from the old action file, and removes that interface from `OrderState`'s implements list. Task 24 sweeps the rest.

---

### Task 6: CreateOrder slice

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Features/CreateOrder/CreateOrder.cs`
- Create: `tests/Alberto.Examples.Tests/Orders/CreateOrderTests.cs`
- Modify: `apps/Alberto.Orders/Alberto.Orders/Features/OrderMutations.cs` (remove `CreateOrder`)
- Modify: `apps/Alberto.Orders/Alberto.Orders/Features/Actions/Create.cs` (remove `ICreateOrderState` and `Create`)
- Modify: `apps/Alberto.Orders/Alberto.Orders/Features/OrderState.cs` (drop `ICreateOrderState`)
- Modify: `apps/Alberto.Orders/Alberto.Orders/Contracts/OrderInputs.cs` (move `CreateOrderInput`, `OrderItemInput`, `CreateOrderResult` out)

**Interfaces:**
- Produces: `Alberto.Orders.Features.CreateOrderState`, `CreateOrderEvolver`, `CreateOrderDecider.Boundary(Guid)`, `CreateOrderDecider.Decide(CreateOrderState, Guid, Guid, IReadOnlyList<OrderLineItem>, string?)`, `CreateOrderMutation.CreateOrder(...)`.
- Consumes: `MutationResult`/`EnsureCommitted` from `Alberto.Examples.Shared` (Task 2); `OrdersModule.ModuleKey` from `Alberto.Orders.Platform` (Task 3).

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Orders/CreateOrderTests.cs`:

```csharp
using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class CreateOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b000-0000-7000-8000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("0197b000-0000-7000-8000-000000000002");

    [Fact]
    public void Creates_an_order_that_does_not_exist_yet()
    {
        var decision = CreateOrderDecider.Decide(
            new CreateOrderState(), OrderId, CustomerId, [], notes: null);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderCreated>()
            .Which.OrderId.Should().Be(OrderId);
    }

    [Fact]
    public void Refuses_an_order_that_already_exists()
    {
        var state = new CreateOrderEvolver()
            .Apply(new CreateOrderState(), new OrderCreated(OrderId, CustomerId, [], null));

        var decision = CreateOrderDecider.Decide(state, OrderId, CustomerId, [], notes: null);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.already-exists");
    }

    [Fact]
    public void Refuses_an_order_with_no_customer()
    {
        var decision = CreateOrderDecider.Decide(
            new CreateOrderState(), OrderId, Guid.Empty, [], notes: null);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.customer-required");
    }
}
```

Confirm the third test's expected code against `Contracts/OrderProblems.cs` — use whatever `CustomerRequired()` actually returns.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~CreateOrderTests"
```

Expected: FAIL to compile — `CreateOrderState`, `CreateOrderEvolver` and `CreateOrderDecider` do not exist.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Orders/Alberto.Orders/Features/CreateOrder/CreateOrder.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>Input for creating an order.</summary>
public sealed record CreateOrderInput(
    Guid CustomerId,
    List<OrderItemInput> LineItems,
    string? Notes);

/// <summary>Input for order line items.</summary>
public sealed record OrderItemInput(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);

/// <summary>Result of a create mutation.</summary>
public readonly record struct CreateOrderResult(Guid OrderId);

/// <summary>
/// Everything creation decides on: whether the order is already there. Not the customer, not
/// the items, not the status — a second OrderCreated is refused whatever any of those say.
/// </summary>
public sealed record CreateOrderState
{
    public Guid OrderId { get; init; }

    public bool Exists => OrderId != Guid.Empty;
}

public sealed class CreateOrderEvolver : Evolver<CreateOrderState>,
    IEvolve<CreateOrderState, OrderCreated>
{
    public CreateOrderState Apply(CreateOrderState s, OrderCreated e) => s with { OrderId = e.OrderId };
}

public static class CreateOrderDecider
{
    /// <summary>
    /// Whole-order boundary: the id is fresh, so this reads empty and the append still fails if
    /// anything claimed the order between the read and the write.
    /// </summary>
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(
        CreateOrderState state,
        Guid orderId,
        Guid customerId,
        IReadOnlyList<OrderLineItem> lineItems,
        string? notes = null)
    {
        if (state.Exists)
            return Decision.Fail(OrderProblems.AlreadyExists(orderId));

        if (customerId == Guid.Empty)
            return Decision.Fail(OrderProblems.CustomerRequired());

        return Decision.Succeed(new OrderCreated(orderId, customerId, lineItems, notes));
    }
}

public static class CreateOrderMutation
{
    [Mutation]
    [GraphQLDescription("Creates a new order with the specified line items.")]
    public static async Task<CreateOrderResult> CreateOrder(
        CreateOrderInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var orderId = Guid.CreateVersion7();
        var lineItems = input.LineItems
            .Select(x => new OrderLineItem(x.ProductId, x.ProductName, x.Quantity, x.UnitPrice))
            .ToList();

        var result = await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(input)
            .Load(CreateOrderDecider.Boundary(orderId), new CreateOrderEvolver())
            .Decide((cmd, state) =>
                CreateOrderDecider.Decide(state, orderId, cmd.CustomerId, lineItems, cmd.Notes))
            .Commit(ct);

        result.EnsureCommitted();
        return new CreateOrderResult(orderId);
    }
}
```

No `RetryOnConflict` here — matching the current `OrderMutations.CreateOrder`, which does not retry.

- [ ] **Step 4: Remove the old copies**

- In `Features/OrderMutations.cs`, delete the `CreateOrder` method (currently lines 27–52).
- In `Features/Actions/Create.cs`, delete the `ICreateOrderState` interface and the `static Decision Create(...)` method. Keep the `Apply(OrderState, OrderCreated)` fragment.
- In `Features/OrderState.cs`, remove `ICreateOrderState,` from the implements list.
- In `Contracts/OrderInputs.cs`, delete `CreateOrderInput`, `OrderItemInput` and `CreateOrderResult` — they now live in the slice.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS, including `Schema_matches_snapshot`. The mutation moved class and file, but the field name, argument name and description did not change.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(orders): CreateOrder owns its state"
```

---

### Task 7: AddOrderItem slice

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Features/AddOrderItem/AddOrderItem.cs`
- Create: `tests/Alberto.Examples.Tests/Orders/AddOrderItemTests.cs`
- Modify: `Features/OrderMutations.cs`, `Features/Actions/AddItem.cs`, `Features/OrderState.cs`, `Contracts/OrderInputs.cs`

**Interfaces:**
- Produces: `AddOrderItemInput`, `AddOrderItemState`, `AddOrderItemEvolver`, `AddOrderItemDecider.Boundary(Guid)`, `AddOrderItemDecider.Decide(AddOrderItemState, Guid, string, int, decimal)`, `AddOrderItemMutation.AddOrderItem(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Orders/AddOrderItemTests.cs`:

```csharp
using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class AddOrderItemTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b001-0000-7000-8000-000000000001");
    private static readonly Guid ProductId = Guid.Parse("0197b001-0000-7000-8000-000000000002");

    private static AddOrderItemState Draft() =>
        new AddOrderItemEvolver().Apply(
            new AddOrderItemState(),
            new OrderCreated(OrderId, Guid.NewGuid(), [], null));

    [Fact]
    public void Adds_an_item_to_a_draft_order()
    {
        var decision = AddOrderItemDecider.Decide(Draft(), ProductId, "Widget", 2, 9.99m);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderItemAdded>()
            .Which.ProductId.Should().Be(ProductId);
    }

    [Fact]
    public void Refuses_a_non_positive_quantity()
    {
        var decision = AddOrderItemDecider.Decide(Draft(), ProductId, "Widget", 0, 9.99m);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.invalid-quantity");
    }

    [Fact]
    public void Reports_the_real_status_when_the_order_is_no_longer_a_draft()
    {
        var evolver = new AddOrderItemEvolver();
        var state = evolver.Apply(Draft(), new OrderConfirmed(OrderId, DateTimeOffset.UnixEpoch));
        state = evolver.Apply(state, new OrderShipped(OrderId, "TRACK", "DHL", DateTimeOffset.UnixEpoch));

        var decision = AddOrderItemDecider.Decide(state, ProductId, "Widget", 1, 1m);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Details!["status"].Should().Be("Shipped");
    }
}
```

The third test is the reason this slice folds every status event: the message and details name the status, so skipping `OrderShipped` would report `Confirmed`.

Check `Problem.Details`' exact member name in `src/Alberto.Dcb/Problem.cs` and adjust the last assertion to match.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~AddOrderItemTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Orders/Alberto.Orders/Features/AddOrderItem/AddOrderItem.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>Input for adding an item to an order.</summary>
public sealed record AddOrderItemInput(
    Guid OrderId,
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);

/// <summary>
/// Adding an item needs the order's identity and its status — and nothing about the items
/// already on it, since adding one never depends on the others.
/// </summary>
public sealed record AddOrderItemState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeModified => Status == OrderStatus.Draft;
}

public sealed class AddOrderItemEvolver : Evolver<AddOrderItemState>,
    IEvolve<AddOrderItemState, OrderCreated>,
    IEvolve<AddOrderItemState, OrderConfirmed>,
    IEvolve<AddOrderItemState, OrderShipped>,
    IEvolve<AddOrderItemState, OrderDelivered>,
    IEvolve<AddOrderItemState, OrderCancelled>
{
    public AddOrderItemState Apply(AddOrderItemState s, OrderCreated e) =>
        s with { OrderId = e.OrderId, Status = OrderStatus.Draft };

    public AddOrderItemState Apply(AddOrderItemState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public AddOrderItemState Apply(AddOrderItemState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public AddOrderItemState Apply(AddOrderItemState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public AddOrderItemState Apply(AddOrderItemState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class AddOrderItemDecider
{
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(
        AddOrderItemState state,
        Guid productId,
        string productName,
        int quantity,
        decimal unitPrice)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeModified)
            return Decision.Fail(OrderProblems.InvalidStatus("modified", state.Status));

        if (quantity <= 0)
            return Decision.Fail(OrderProblems.InvalidQuantity());

        if (unitPrice < 0)
            return Decision.Fail(OrderProblems.InvalidUnitPrice());

        return Decision.Succeed(
            new OrderItemAdded(state.OrderId, productId, productName, quantity, unitPrice));
    }
}

public static class AddOrderItemMutation
{
    private const int ConflictRetries = 3;

    [Mutation]
    [GraphQLDescription("Adds a line item to an existing draft order.")]
    public static async Task<MutationResult> AddOrderItem(
        AddOrderItemInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(input)
            .Load(cmd => AddOrderItemDecider.Boundary(cmd.OrderId), new AddOrderItemEvolver())
            .Decide((cmd, state) => AddOrderItemDecider.Decide(
                state, cmd.ProductId, cmd.ProductName, cmd.Quantity, cmd.UnitPrice))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
```

The `Load(Func<TCommand, DcbQuery>, Evolver<TState>)` overload takes the evolver per call site, so a new `AddOrderItemEvolver()` here needs no DI registration.

- [ ] **Step 4: Remove the old copies**

- Delete `AddOrderItem` from `Features/OrderMutations.cs` (currently lines 54–74).
- In `Features/Actions/AddItem.cs`, delete `IAddItemState` and `static Decision AddItem(...)`; keep the `Apply` fragment.
- In `Features/OrderState.cs`, remove `IAddItemState,`.
- In `Contracts/OrderInputs.cs`, delete `AddOrderItemInput`.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(orders): AddOrderItem owns its state"
```

---

### Task 8: RemoveOrderItem slice

This slice needs to know which products are on the order — but only their ids, not names, quantities or prices.

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Features/RemoveOrderItem/RemoveOrderItem.cs`
- Create: `tests/Alberto.Examples.Tests/Orders/RemoveOrderItemTests.cs`
- Modify: `Features/OrderMutations.cs`, `Features/Actions/RemoveItem.cs`, `Features/OrderState.cs`

**Interfaces:**
- Produces: `RemoveOrderItemState` (carries `IReadOnlyList<Guid> ProductIds`), `RemoveOrderItemEvolver`, `RemoveOrderItemDecider.Boundary(Guid)`, `RemoveOrderItemDecider.Decide(RemoveOrderItemState, Guid)`, `RemoveOrderItemMutation.RemoveOrderItem(...)`.
- Note: this mutation takes loose `Guid orderId, Guid productId` arguments rather than an input record. That is the current schema; do not "improve" it.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Orders/RemoveOrderItemTests.cs`:

```csharp
using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class RemoveOrderItemTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b002-0000-7000-8000-000000000001");
    private static readonly Guid ProductId = Guid.Parse("0197b002-0000-7000-8000-000000000002");

    private static RemoveOrderItemState WithOneItem()
    {
        var evolver = new RemoveOrderItemEvolver();
        var state = evolver.Apply(
            new RemoveOrderItemState(),
            new OrderCreated(OrderId, Guid.NewGuid(), [], null));

        return evolver.Apply(
            state, new OrderItemAdded(OrderId, ProductId, "Widget", 1, 9.99m));
    }

    [Fact]
    public void Removes_an_item_that_is_on_the_order()
    {
        var decision = RemoveOrderItemDecider.Decide(WithOneItem(), ProductId);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderItemRemoved>();
    }

    [Fact]
    public void Refuses_a_product_that_is_not_on_the_order()
    {
        var decision = RemoveOrderItemDecider.Decide(WithOneItem(), Guid.NewGuid());

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.product-not-found");
    }

    [Fact]
    public void Forgets_an_item_that_was_already_removed()
    {
        var state = new RemoveOrderItemEvolver()
            .Apply(WithOneItem(), new OrderItemRemoved(OrderId, ProductId));

        RemoveOrderItemDecider.Decide(state, ProductId).IsError.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~RemoveOrderItemTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Orders/Alberto.Orders/Features/RemoveOrderItem/RemoveOrderItem.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>
/// Removal needs to know whether the product is on the order, which is a set of ids — not the
/// line items. Names, quantities and prices are in the same events and are not folded.
/// </summary>
public sealed record RemoveOrderItemState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;
    public IReadOnlyList<Guid> ProductIds { get; init; } = [];

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeModified => Status == OrderStatus.Draft;
}

public sealed class RemoveOrderItemEvolver : Evolver<RemoveOrderItemState>,
    IEvolve<RemoveOrderItemState, OrderCreated>,
    IEvolve<RemoveOrderItemState, OrderItemAdded>,
    IEvolve<RemoveOrderItemState, OrderItemRemoved>,
    IEvolve<RemoveOrderItemState, OrderConfirmed>,
    IEvolve<RemoveOrderItemState, OrderShipped>,
    IEvolve<RemoveOrderItemState, OrderDelivered>,
    IEvolve<RemoveOrderItemState, OrderCancelled>
{
    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderCreated e) => s with
    {
        OrderId = e.OrderId,
        Status = OrderStatus.Draft,
        ProductIds = e.LineItems.Select(x => x.ProductId).ToList()
    };

    // Mirrors OrderItemAdded's semantics: adding a product already on the order replaces it.
    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderItemAdded e) => s with
    {
        ProductIds = s.ProductIds.Where(id => id != e.ProductId).Append(e.ProductId).ToList()
    };

    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderItemRemoved e) => s with
    {
        ProductIds = s.ProductIds.Where(id => id != e.ProductId).ToList()
    };

    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public RemoveOrderItemState Apply(RemoveOrderItemState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class RemoveOrderItemDecider
{
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(RemoveOrderItemState state, Guid productId)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeModified)
            return Decision.Fail(OrderProblems.InvalidStatus("modified", state.Status));

        if (state.ProductIds.All(id => id != productId))
            return Decision.Fail(OrderProblems.ProductNotFound(productId));

        return Decision.Succeed(new OrderItemRemoved(state.OrderId, productId));
    }
}

public static class RemoveOrderItemMutation
{
    private const int ConflictRetries = 3;

    [Mutation]
    [GraphQLDescription("Removes a line item from a draft order.")]
    public static async Task<MutationResult> RemoveOrderItem(
        Guid orderId,
        Guid productId,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(productId)
            .Load(RemoveOrderItemDecider.Boundary(orderId), new RemoveOrderItemEvolver())
            .Decide((product, state) => RemoveOrderItemDecider.Decide(state, product))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
```

- [ ] **Step 4: Remove the old copies**

- Delete `RemoveOrderItem` from `Features/OrderMutations.cs` (currently lines 76–96).
- In `Features/Actions/RemoveItem.cs`, delete `IRemoveItemState` and `static Decision RemoveItem(...)`; keep the `Apply` fragment.
- In `Features/OrderState.cs`, remove `IRemoveItemState,`.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(orders): RemoveOrderItem owns its state"
```

---

### Task 9: ConfirmOrder slice

Confirmation's guard is "draft **and** not empty", and its refusal branches on which of the two failed — so it needs the item ids, like removal, and nothing else about them.

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Features/ConfirmOrder/ConfirmOrder.cs`
- Create: `tests/Alberto.Examples.Tests/Orders/ConfirmOrderTests.cs`
- Modify: `Features/OrderMutations.cs`, `Features/Actions/Confirm.cs`, `Features/OrderState.cs`

**Interfaces:**
- Produces: `ConfirmOrderState` (`ProductIds`, `Status`), `ConfirmOrderEvolver`, `ConfirmOrderDecider.Boundary(Guid)`, `ConfirmOrderDecider.Decide(ConfirmOrderState, DateTimeOffset)`, `ConfirmOrderMutation.ConfirmOrder(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Orders/ConfirmOrderTests.cs`:

```csharp
using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class ConfirmOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b003-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static ConfirmOrderState Created(params OrderLineItem[] items) =>
        new ConfirmOrderEvolver().Apply(
            new ConfirmOrderState(),
            new OrderCreated(OrderId, Guid.NewGuid(), items, null));

    [Fact]
    public void Confirms_a_draft_order_that_has_items()
    {
        var state = Created(new OrderLineItem(Guid.NewGuid(), "Widget", 1, 9.99m));

        var decision = ConfirmOrderDecider.Decide(state, Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderConfirmed>();
    }

    [Fact]
    public void Refuses_an_empty_order_with_the_empty_problem_not_the_status_one()
    {
        var decision = ConfirmOrderDecider.Decide(Created(), Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.empty");
    }

    [Fact]
    public void Refuses_an_order_that_is_already_confirmed()
    {
        var evolver = new ConfirmOrderEvolver();
        var state = evolver.Apply(
            Created(new OrderLineItem(Guid.NewGuid(), "Widget", 1, 9.99m)),
            new OrderConfirmed(OrderId, Now));

        var decision = ConfirmOrderDecider.Decide(state, Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.invalid-status");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~ConfirmOrderTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Orders/Alberto.Orders/Features/ConfirmOrder/ConfirmOrder.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>
/// Confirmation refuses an empty order differently from a non-draft one, so it needs to know
/// both the status and whether any items are on the order — as ids, since the count is all the
/// guard asks and the ids are what keep add-then-add-again from counting twice.
/// </summary>
public sealed record ConfirmOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;
    public IReadOnlyList<Guid> ProductIds { get; init; } = [];

    public bool Exists => OrderId != Guid.Empty;
    public bool IsEmpty => ProductIds.Count == 0;
    public bool CanBeConfirmed => Status == OrderStatus.Draft && !IsEmpty;
}

public sealed class ConfirmOrderEvolver : Evolver<ConfirmOrderState>,
    IEvolve<ConfirmOrderState, OrderCreated>,
    IEvolve<ConfirmOrderState, OrderItemAdded>,
    IEvolve<ConfirmOrderState, OrderItemRemoved>,
    IEvolve<ConfirmOrderState, OrderConfirmed>,
    IEvolve<ConfirmOrderState, OrderShipped>,
    IEvolve<ConfirmOrderState, OrderDelivered>,
    IEvolve<ConfirmOrderState, OrderCancelled>
{
    public ConfirmOrderState Apply(ConfirmOrderState s, OrderCreated e) => s with
    {
        OrderId = e.OrderId,
        Status = OrderStatus.Draft,
        ProductIds = e.LineItems.Select(x => x.ProductId).ToList()
    };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderItemAdded e) => s with
    {
        ProductIds = s.ProductIds.Where(id => id != e.ProductId).Append(e.ProductId).ToList()
    };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderItemRemoved e) => s with
    {
        ProductIds = s.ProductIds.Where(id => id != e.ProductId).ToList()
    };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public ConfirmOrderState Apply(ConfirmOrderState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class ConfirmOrderDecider
{
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(ConfirmOrderState state, DateTimeOffset confirmedAt)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeConfirmed)
            return Decision.Fail(state.IsEmpty
                ? OrderProblems.Empty()
                : OrderProblems.InvalidStatus("confirmed", state.Status));

        return Decision.Succeed(new OrderConfirmed(state.OrderId, confirmedAt));
    }
}

public static class ConfirmOrderMutation
{
    private const int ConflictRetries = 3;

    [Mutation]
    [GraphQLDescription("Confirms a draft order, making it ready for shipment.")]
    public static async Task<MutationResult> ConfirmOrder(
        Guid orderId,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(orderId)
            .Load(ConfirmOrderDecider.Boundary(orderId), new ConfirmOrderEvolver())
            .Decide(state => ConfirmOrderDecider.Decide(state, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
```

Note the behavioural equivalence: the old `Confirm` chose `Empty()` when `LineItems.Count == 0`, which is `IsEmpty` here.

- [ ] **Step 4: Remove the old copies**

- Delete `ConfirmOrder` from `Features/OrderMutations.cs` (currently lines 98–118).
- In `Features/Actions/Confirm.cs`, delete `IConfirmOrderState` and `static Decision Confirm(...)`; keep the `Apply` fragment.
- In `Features/OrderState.cs`, remove `IConfirmOrderState,`.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(orders): ConfirmOrder owns its state"
```

---

### Task 10: ShipOrder slice

This is the slice the spec used as its example. The spec's sketch folds four events; this folds five. `OrderDelivered` is included because the refusal message names the status, and shipping an already-delivered order must say `Delivered`, not `Shipped`.

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Features/ShipOrder/ShipOrder.cs`
- Create: `tests/Alberto.Examples.Tests/Orders/ShipOrderTests.cs`
- Modify: `Features/OrderMutations.cs`, `Features/Actions/Ship.cs`, `Features/OrderState.cs`, `Contracts/OrderInputs.cs`

**Interfaces:**
- Produces: `ShipOrderInput`, `ShipOrderState` (`OrderId`, `Status` — two properties), `ShipOrderEvolver`, `ShipOrderDecider.Boundary(Guid)`, `ShipOrderDecider.Decide(ShipOrderState, string, string, DateTimeOffset)`, `ShipOrderMutation.ShipOrder(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Orders/ShipOrderTests.cs`:

```csharp
using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class ShipOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b004-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static ShipOrderState Confirmed()
    {
        var evolver = new ShipOrderEvolver();
        var state = evolver.Apply(
            new ShipOrderState(), new OrderCreated(OrderId, Guid.NewGuid(), [], null));

        return evolver.Apply(state, new OrderConfirmed(OrderId, Now));
    }

    [Fact]
    public void Ships_a_confirmed_order()
    {
        var decision = ShipOrderDecider.Decide(Confirmed(), "TRACK-1", "DHL", Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderShipped>()
            .Which.TrackingNumber.Should().Be("TRACK-1");
    }

    [Fact]
    public void Requires_a_tracking_number()
    {
        var decision = ShipOrderDecider.Decide(Confirmed(), "  ", "DHL", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.tracking-number-required");
    }

    [Fact]
    public void Reports_Delivered_when_the_order_has_already_been_delivered()
    {
        var evolver = new ShipOrderEvolver();
        var state = evolver.Apply(Confirmed(), new OrderShipped(OrderId, "TRACK-1", "DHL", Now));
        state = evolver.Apply(state, new OrderDelivered(OrderId, Now));

        var decision = ShipOrderDecider.Decide(state, "TRACK-2", "DHL", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Message.Should().Contain("Delivered");
    }

    [Fact]
    public void Ignores_line_item_events_entirely()
    {
        var state = Confirmed();
        var evolved = new ShipOrderEvolver().Apply(state, new OrderItemAdded(
            OrderId, Guid.NewGuid(), "Widget", 1, 9.99m));

        evolved.Should().Be(state, "shipping does not depend on what is in the order");
    }
}
```

The last test only compiles if `ShipOrderEvolver` has no `Apply` overload for `OrderItemAdded` — so write it as a call through `Evolve`/`Reconstitute` instead if the direct overload does not exist. Simplest form that expresses the same thing without an overload:

```csharp
    [Fact]
    public void Handles_only_the_status_events()
    {
        new ShipOrderEvolver().HandledEventTypes.Should().BeEquivalentTo(
            ["order-created", "order-confirmed", "order-shipped", "order-delivered", "order-cancelled"]);
    }
```

Use this second form — `Evolver<TState>.HandledEventTypes` is public and says exactly what the slice ignores.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~ShipOrderTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Orders/Alberto.Orders/Features/ShipOrder/ShipOrder.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>Input for shipping an order.</summary>
public sealed record ShipOrderInput(
    Guid OrderId,
    string TrackingNumber,
    string Carrier);

/// <summary>
/// Two properties. Shipping never sees LineItems, Notes, CustomerId or the timestamps of the
/// other transitions.
/// </summary>
public sealed record ShipOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeShipped => Status == OrderStatus.Confirmed;
}

/// <remarks>
/// OrderItemAdded and OrderItemRemoved are ignored: they cannot change whether an order is
/// shippable. OrderDelivered can't either — but the refusal message names the status, so
/// leaving it out would tell a client a delivered order "cannot be shipped in Shipped status".
/// </remarks>
public sealed class ShipOrderEvolver : Evolver<ShipOrderState>,
    IEvolve<ShipOrderState, OrderCreated>,
    IEvolve<ShipOrderState, OrderConfirmed>,
    IEvolve<ShipOrderState, OrderShipped>,
    IEvolve<ShipOrderState, OrderDelivered>,
    IEvolve<ShipOrderState, OrderCancelled>
{
    public ShipOrderState Apply(ShipOrderState s, OrderCreated e) =>
        s with { OrderId = e.OrderId, Status = OrderStatus.Draft };

    public ShipOrderState Apply(ShipOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public ShipOrderState Apply(ShipOrderState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public ShipOrderState Apply(ShipOrderState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public ShipOrderState Apply(ShipOrderState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class ShipOrderDecider
{
    /// <summary>
    /// Whole-order boundary: any concurrent order event conflicts with a ship. Narrowing the
    /// type axis would let non-overlapping slices commit together, but every event that could
    /// invalidate this decision would have to be listed here or the decision silently loses
    /// updates. That argument is per slice; this one has not been made.
    /// </summary>
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(
        ShipOrderState state,
        string trackingNumber,
        string carrier,
        DateTimeOffset shippedAt)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeShipped)
            return Decision.Fail(OrderProblems.InvalidStatus("shipped", state.Status));

        if (string.IsNullOrWhiteSpace(trackingNumber))
            return Decision.Fail(OrderProblems.TrackingNumberRequired());

        if (string.IsNullOrWhiteSpace(carrier))
            return Decision.Fail(OrderProblems.CarrierRequired());

        return Decision.Succeed(
            new OrderShipped(state.OrderId, trackingNumber, carrier, shippedAt));
    }
}

public static class ShipOrderMutation
{
    private const int ConflictRetries = 3;

    [Mutation]
    [GraphQLDescription("Ships a confirmed order with tracking information.")]
    public static async Task<MutationResult> ShipOrder(
        ShipOrderInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(input)
            .Load(cmd => ShipOrderDecider.Boundary(cmd.OrderId), new ShipOrderEvolver())
            .Decide((cmd, state) => ShipOrderDecider.Decide(
                state, cmd.TrackingNumber, cmd.Carrier, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
```

- [ ] **Step 4: Remove the old copies**

- Delete `ShipOrder` from `Features/OrderMutations.cs` (currently lines 120–141).
- In `Features/Actions/Ship.cs`, delete `IShipOrderState` and `static Decision Ship(...)`; keep the `Apply` fragment.
- In `Features/OrderState.cs`, remove `IShipOrderState,`.
- In `Contracts/OrderInputs.cs`, delete `ShipOrderInput`.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(orders): ShipOrder owns its state"
```

---

### Task 11: DeliverOrder slice

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Features/DeliverOrder/DeliverOrder.cs`
- Create: `tests/Alberto.Examples.Tests/Orders/DeliverOrderTests.cs`
- Modify: `Features/OrderMutations.cs`, `Features/Actions/Deliver.cs`, `Features/OrderState.cs`

**Interfaces:**
- Produces: `DeliverOrderState` (`OrderId`, `Status`), `DeliverOrderEvolver`, `DeliverOrderDecider.Boundary(Guid)`, `DeliverOrderDecider.Decide(DeliverOrderState, DateTimeOffset)`, `DeliverOrderMutation.DeliverOrder(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Orders/DeliverOrderTests.cs`:

```csharp
using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class DeliverOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b005-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static DeliverOrderState Shipped()
    {
        var evolver = new DeliverOrderEvolver();
        var state = evolver.Apply(
            new DeliverOrderState(), new OrderCreated(OrderId, Guid.NewGuid(), [], null));
        state = evolver.Apply(state, new OrderConfirmed(OrderId, Now));

        return evolver.Apply(state, new OrderShipped(OrderId, "TRACK-1", "DHL", Now));
    }

    [Fact]
    public void Delivers_a_shipped_order()
    {
        var decision = DeliverOrderDecider.Decide(Shipped(), Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderDelivered>();
    }

    [Fact]
    public void Reports_Confirmed_for_an_order_that_was_never_shipped()
    {
        var evolver = new DeliverOrderEvolver();
        var state = evolver.Apply(
            new DeliverOrderState(), new OrderCreated(OrderId, Guid.NewGuid(), [], null));
        state = evolver.Apply(state, new OrderConfirmed(OrderId, Now));

        var decision = DeliverOrderDecider.Decide(state, Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Message.Should().Contain("Confirmed");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~DeliverOrderTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Orders/Alberto.Orders/Features/DeliverOrder/DeliverOrder.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>
/// Delivery reads the same two properties as shipping, and folds them from its own evolver. The
/// shape being identical to ShipOrderState is not a reason to share one: they are free to
/// diverge, and a shared record is what made every action carry every field.
/// </summary>
public sealed record DeliverOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeDelivered => Status == OrderStatus.Shipped;
}

public sealed class DeliverOrderEvolver : Evolver<DeliverOrderState>,
    IEvolve<DeliverOrderState, OrderCreated>,
    IEvolve<DeliverOrderState, OrderConfirmed>,
    IEvolve<DeliverOrderState, OrderShipped>,
    IEvolve<DeliverOrderState, OrderDelivered>,
    IEvolve<DeliverOrderState, OrderCancelled>
{
    public DeliverOrderState Apply(DeliverOrderState s, OrderCreated e) =>
        s with { OrderId = e.OrderId, Status = OrderStatus.Draft };

    public DeliverOrderState Apply(DeliverOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public DeliverOrderState Apply(DeliverOrderState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public DeliverOrderState Apply(DeliverOrderState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public DeliverOrderState Apply(DeliverOrderState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class DeliverOrderDecider
{
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(DeliverOrderState state, DateTimeOffset deliveredAt)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeDelivered)
            return Decision.Fail(OrderProblems.InvalidStatus("delivered", state.Status));

        return Decision.Succeed(new OrderDelivered(state.OrderId, deliveredAt));
    }
}

public static class DeliverOrderMutation
{
    private const int ConflictRetries = 3;

    [Mutation]
    [GraphQLDescription("Marks a shipped order as delivered.")]
    public static async Task<MutationResult> DeliverOrder(
        Guid orderId,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(orderId)
            .Load(DeliverOrderDecider.Boundary(orderId), new DeliverOrderEvolver())
            .Decide(state => DeliverOrderDecider.Decide(state, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
```

- [ ] **Step 4: Remove the old copies**

- Delete `DeliverOrder` from `Features/OrderMutations.cs` (currently lines 143–163).
- In `Features/Actions/Deliver.cs`, delete `IDeliverOrderState` and `static Decision Deliver(...)`; keep the `Apply` fragment.
- In `Features/OrderState.cs`, remove `IDeliverOrderState,`.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(orders): DeliverOrder owns its state"
```

---

### Task 12: CancelOrder slice

The last Orders write slice. After this, `OrderMutations.cs` is empty and gets deleted.

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Features/CancelOrder/CancelOrder.cs`
- Create: `tests/Alberto.Examples.Tests/Orders/CancelOrderTests.cs`
- Delete: `apps/Alberto.Orders/Alberto.Orders/Features/OrderMutations.cs`
- Modify: `Features/Actions/Cancel.cs`, `Features/OrderState.cs`, `Contracts/OrderInputs.cs`

**Interfaces:**
- Produces: `CancelOrderInput`, `CancelOrderState`, `CancelOrderEvolver`, `CancelOrderDecider.Boundary(Guid)`, `CancelOrderDecider.Decide(CancelOrderState, string, DateTimeOffset)`, `CancelOrderMutation.CancelOrder(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Orders/CancelOrderTests.cs`:

```csharp
using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class CancelOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b006-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static CancelOrderState Draft() =>
        new CancelOrderEvolver().Apply(
            new CancelOrderState(), new OrderCreated(OrderId, Guid.NewGuid(), [], null));

    [Fact]
    public void Cancels_a_draft_order()
    {
        var decision = CancelOrderDecider.Decide(Draft(), "changed my mind", Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<OrderCancelled>()
            .Which.Reason.Should().Be("changed my mind");
    }

    [Fact]
    public void Requires_a_reason()
    {
        var decision = CancelOrderDecider.Decide(Draft(), "   ", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.cancellation-reason-required");
    }

    [Fact]
    public void Refuses_to_cancel_a_shipped_order()
    {
        var evolver = new CancelOrderEvolver();
        var state = evolver.Apply(Draft(), new OrderConfirmed(OrderId, Now));
        state = evolver.Apply(state, new OrderShipped(OrderId, "TRACK-1", "DHL", Now));

        var decision = CancelOrderDecider.Decide(state, "too late", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("order.invalid-status");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~CancelOrderTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Orders/Alberto.Orders/Features/CancelOrder/CancelOrder.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>Input for cancelling an order.</summary>
public sealed record CancelOrderInput(
    Guid OrderId,
    string Reason);

/// <summary>
/// Cancellation is allowed from two statuses and refused from three, so status is all it folds.
/// It does not carry the reason it is about to record — nothing about the decision depends on a
/// previous cancellation's reason.
/// </summary>
public sealed record CancelOrderState
{
    public Guid OrderId { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;

    public bool Exists => OrderId != Guid.Empty;
    public bool CanBeCancelled => Status is OrderStatus.Draft or OrderStatus.Confirmed;
}

public sealed class CancelOrderEvolver : Evolver<CancelOrderState>,
    IEvolve<CancelOrderState, OrderCreated>,
    IEvolve<CancelOrderState, OrderConfirmed>,
    IEvolve<CancelOrderState, OrderShipped>,
    IEvolve<CancelOrderState, OrderDelivered>,
    IEvolve<CancelOrderState, OrderCancelled>
{
    public CancelOrderState Apply(CancelOrderState s, OrderCreated e) =>
        s with { OrderId = e.OrderId, Status = OrderStatus.Draft };

    public CancelOrderState Apply(CancelOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed };

    public CancelOrderState Apply(CancelOrderState s, OrderShipped e) =>
        s with { Status = OrderStatus.Shipped };

    public CancelOrderState Apply(CancelOrderState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered };

    public CancelOrderState Apply(CancelOrderState s, OrderCancelled e) =>
        s with { Status = OrderStatus.Cancelled };
}

public static class CancelOrderDecider
{
    public static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    public static Decision Decide(
        CancelOrderState state,
        string reason,
        DateTimeOffset cancelledAt)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.NotFound());

        if (!state.CanBeCancelled)
            return Decision.Fail(OrderProblems.InvalidStatus("cancelled", state.Status));

        if (string.IsNullOrWhiteSpace(reason))
            return Decision.Fail(OrderProblems.CancellationReasonRequired());

        return Decision.Succeed(new OrderCancelled(state.OrderId, reason, cancelledAt));
    }
}

public static class CancelOrderMutation
{
    private const int ConflictRetries = 3;

    [Mutation]
    [GraphQLDescription("Cancels a draft or confirmed order.")]
    public static async Task<MutationResult> CancelOrder(
        CancelOrderInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(OrdersModule.ModuleKey)
            .Handle(input)
            .Load(cmd => CancelOrderDecider.Boundary(cmd.OrderId), new CancelOrderEvolver())
            .Decide((cmd, state) =>
                CancelOrderDecider.Decide(state, cmd.Reason, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
```

- [ ] **Step 4: Remove the old copies**

```bash
git rm apps/Alberto.Orders/Alberto.Orders/Features/OrderMutations.cs
```

- In `Features/Actions/Cancel.cs`, delete `ICancelOrderState` and `static Decision Cancel(...)`; keep the `Apply` fragment.
- In `Features/OrderState.cs`, the implements list is now empty — reduce the declaration to `public sealed record OrderState`.
- In `Contracts/OrderInputs.cs`, delete `CancelOrderInput`. The file should now be empty; delete it too.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS. All seven order mutations now live in their own slice files and the schema is unchanged.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(orders): CancelOrder owns its state"
```

---
### Task 13: InitiatePayment slice

**Files:**
- Create: `apps/Alberto.Payments/Alberto.Payments/Features/InitiatePayment/InitiatePayment.cs`
- Create: `tests/Alberto.Examples.Tests/Payments/InitiatePaymentTests.cs`
- Modify: `apps/Alberto.Payments/Alberto.Payments/Features/PaymentMutations.cs`, `Features/Actions/Initiate.cs`, `Features/PaymentState.cs`, `Contracts/PaymentInputs.cs`

**Interfaces:**
- Produces: `InitiatePaymentInput`, `InitiatePaymentResult`, `InitiatePaymentState`, `InitiatePaymentEvolver`, `InitiatePaymentDecider.Boundary(Guid)`, `InitiatePaymentDecider.Decide(InitiatePaymentState, Guid, Guid, decimal, string, string)`, `InitiatePaymentMutation.InitiatePayment(...)`.
- Consumes: `MutationResult`/`EnsureCommitted` (Task 2), `PaymentsModule.ModuleKey` (Task 4).

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Payments/InitiatePaymentTests.cs`:

```csharp
using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class InitiatePaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c000-0000-7000-8000-000000000001");
    private static readonly Guid OrderId = Guid.Parse("0197c000-0000-7000-8000-000000000002");

    [Fact]
    public void Initiates_a_payment_that_does_not_exist_yet()
    {
        var decision = InitiatePaymentDecider.Decide(
            new InitiatePaymentState(), PaymentId, OrderId, 10m, "EUR", "card");

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentInitiated>();
    }

    [Fact]
    public void Refuses_a_payment_that_already_exists()
    {
        var state = new InitiatePaymentEvolver().Apply(
            new InitiatePaymentState(),
            new PaymentInitiated(PaymentId, OrderId, 10m, "EUR", "card"));

        var decision = InitiatePaymentDecider.Decide(
            state, PaymentId, OrderId, 10m, "EUR", "card");

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.already-exists");
    }

    [Fact]
    public void Refuses_a_non_positive_amount()
    {
        var decision = InitiatePaymentDecider.Decide(
            new InitiatePaymentState(), PaymentId, OrderId, 0m, "EUR", "card");

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.invalid-amount");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~InitiatePaymentTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Payments/Alberto.Payments/Features/InitiatePayment/InitiatePayment.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;

namespace Alberto.Payments.Features;

/// <summary>Input for initiating a payment.</summary>
public sealed record InitiatePaymentInput(
    Guid OrderId,
    decimal Amount,
    string Currency,
    string PaymentMethod);

/// <summary>Result of initiating a payment.</summary>
public readonly record struct InitiatePaymentResult(Guid PaymentId);

/// <summary>
/// Initiation decides on existence alone: a second PaymentInitiated is refused whatever
/// status, amount or currency the first one carried.
/// </summary>
public sealed record InitiatePaymentState
{
    public Guid PaymentId { get; init; }

    public bool Exists => PaymentId != Guid.Empty;
}

public sealed class InitiatePaymentEvolver : Evolver<InitiatePaymentState>,
    IEvolve<InitiatePaymentState, PaymentInitiated>
{
    public InitiatePaymentState Apply(InitiatePaymentState s, PaymentInitiated e) =>
        s with { PaymentId = e.PaymentId };
}

public static class InitiatePaymentDecider
{
    public static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    public static Decision Decide(
        InitiatePaymentState state,
        Guid paymentId,
        Guid orderId,
        decimal amount,
        string currency,
        string paymentMethod)
    {
        if (state.Exists)
            return Decision.Fail(PaymentProblems.AlreadyExists(paymentId));

        if (orderId == Guid.Empty)
            return Decision.Fail(PaymentProblems.OrderRequired());

        if (amount <= 0)
            return Decision.Fail(PaymentProblems.InvalidAmount());

        if (string.IsNullOrWhiteSpace(currency))
            return Decision.Fail(PaymentProblems.CurrencyRequired());

        if (string.IsNullOrWhiteSpace(paymentMethod))
            return Decision.Fail(PaymentProblems.PaymentMethodRequired());

        return Decision.Succeed(
            new PaymentInitiated(paymentId, orderId, amount, currency, paymentMethod));
    }
}

public static class InitiatePaymentMutation
{
    [Mutation]
    [GraphQLDescription("Initiates a new payment for an order.")]
    public static async Task<InitiatePaymentResult> InitiatePayment(
        InitiatePaymentInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var paymentId = Guid.CreateVersion7();

        var result = await sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey)
            .Handle(input)
            .Load(InitiatePaymentDecider.Boundary(paymentId), new InitiatePaymentEvolver())
            .Decide((cmd, state) => InitiatePaymentDecider.Decide(
                state, paymentId, cmd.OrderId, cmd.Amount, cmd.Currency, cmd.PaymentMethod))
            .Commit(ct);

        result.EnsureCommitted();
        return new InitiatePaymentResult(paymentId);
    }
}
```

- [ ] **Step 4: Remove the old copies**

- Delete `InitiatePayment` from `Features/PaymentMutations.cs`.
- In `Features/Actions/Initiate.cs`, delete `IInitiatePaymentState` and `static Decision Initiate(...)`; keep the `Apply` fragment.
- In `Features/PaymentState.cs`, remove `IInitiatePaymentState,`.
- In `Contracts/PaymentInputs.cs`, delete `InitiatePaymentInput` and `InitiatePaymentResult`.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(payments): InitiatePayment owns its state"
```

---

### Task 14: AuthorizePayment slice

**Files:**
- Create: `apps/Alberto.Payments/Alberto.Payments/Features/AuthorizePayment/AuthorizePayment.cs`
- Create: `tests/Alberto.Examples.Tests/Payments/AuthorizePaymentTests.cs`
- Modify: `Features/PaymentMutations.cs`, `Features/Actions/Authorize.cs`, `Features/PaymentState.cs`

**Interfaces:**
- Produces: `AuthorizePaymentState` (`PaymentId`, `Status`), `AuthorizePaymentEvolver`, `AuthorizePaymentDecider.Boundary(Guid)`, `AuthorizePaymentDecider.Decide(AuthorizePaymentState, string, DateTimeOffset)`, `AuthorizePaymentMutation.AuthorizePayment(...)`.
- Note: the mutation takes loose `Guid paymentId, string authorizationCode` arguments — no input record. Keep it.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Payments/AuthorizePaymentTests.cs`:

```csharp
using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class AuthorizePaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c001-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static AuthorizePaymentState Initiated() =>
        new AuthorizePaymentEvolver().Apply(
            new AuthorizePaymentState(),
            new PaymentInitiated(PaymentId, Guid.NewGuid(), 10m, "EUR", "card"));

    [Fact]
    public void Authorizes_an_initiated_payment()
    {
        var decision = AuthorizePaymentDecider.Decide(Initiated(), "AUTH-1", Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentAuthorized>();
    }

    [Fact]
    public void Requires_an_authorization_code()
    {
        var decision = AuthorizePaymentDecider.Decide(Initiated(), " ", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.authorization-code-required");
    }

    [Fact]
    public void Reports_Refunded_for_a_payment_that_has_been_refunded()
    {
        var evolver = new AuthorizePaymentEvolver();
        var state = evolver.Apply(Initiated(), new PaymentAuthorized(PaymentId, "AUTH-1", Now));
        state = evolver.Apply(state, new PaymentCaptured(PaymentId, 10m, Now));
        state = evolver.Apply(state, new PaymentRefunded(PaymentId, 10m, "duplicate", Now));

        var decision = AuthorizePaymentDecider.Decide(state, "AUTH-2", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Message.Should().Contain("Refunded");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~AuthorizePaymentTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Payments/Alberto.Payments/Features/AuthorizePayment/AuthorizePayment.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;

namespace Alberto.Payments.Features;

/// <summary>
/// Authorization needs the payment's identity and status. Amount, currency, method and the
/// capture/refund figures belong to other slices.
/// </summary>
public sealed record AuthorizePaymentState
{
    public Guid PaymentId { get; init; }
    public PaymentStatus Status { get; init; } = PaymentStatus.None;

    public bool Exists => PaymentId != Guid.Empty;
    public bool CanBeAuthorized => Status == PaymentStatus.Initiated;
}

public sealed class AuthorizePaymentEvolver : Evolver<AuthorizePaymentState>,
    IEvolve<AuthorizePaymentState, PaymentInitiated>,
    IEvolve<AuthorizePaymentState, PaymentAuthorized>,
    IEvolve<AuthorizePaymentState, PaymentCaptured>,
    IEvolve<AuthorizePaymentState, PaymentFailed>,
    IEvolve<AuthorizePaymentState, PaymentRefunded>
{
    public AuthorizePaymentState Apply(AuthorizePaymentState s, PaymentInitiated e) =>
        s with { PaymentId = e.PaymentId, Status = PaymentStatus.Initiated };

    public AuthorizePaymentState Apply(AuthorizePaymentState s, PaymentAuthorized e) =>
        s with { Status = PaymentStatus.Authorized };

    public AuthorizePaymentState Apply(AuthorizePaymentState s, PaymentCaptured e) =>
        s with { Status = PaymentStatus.Captured };

    public AuthorizePaymentState Apply(AuthorizePaymentState s, PaymentFailed e) =>
        s with { Status = PaymentStatus.Failed };

    public AuthorizePaymentState Apply(AuthorizePaymentState s, PaymentRefunded e) =>
        s with { Status = PaymentStatus.Refunded };
}

public static class AuthorizePaymentDecider
{
    public static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    public static Decision Decide(
        AuthorizePaymentState state,
        string authorizationCode,
        DateTimeOffset authorizedAt)
    {
        if (!state.Exists)
            return Decision.Fail(PaymentProblems.NotFound());

        if (!state.CanBeAuthorized)
            return Decision.Fail(PaymentProblems.InvalidStatus("authorized", state.Status));

        if (string.IsNullOrWhiteSpace(authorizationCode))
            return Decision.Fail(PaymentProblems.AuthorizationCodeRequired());

        return Decision.Succeed(
            new PaymentAuthorized(state.PaymentId, authorizationCode, authorizedAt));
    }
}

public static class AuthorizePaymentMutation
{
    private const int ConflictRetries = 3;

    [Mutation]
    [GraphQLDescription("Authorizes a payment with an authorization code.")]
    public static async Task<MutationResult> AuthorizePayment(
        Guid paymentId,
        string authorizationCode,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey)
            .Handle(authorizationCode)
            .Load(AuthorizePaymentDecider.Boundary(paymentId), new AuthorizePaymentEvolver())
            .Decide((code, state) =>
                AuthorizePaymentDecider.Decide(state, code, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
```

- [ ] **Step 4: Remove the old copies**

- Delete `AuthorizePayment` from `Features/PaymentMutations.cs`.
- In `Features/Actions/Authorize.cs`, delete `IAuthorizePaymentState` and `static Decision Authorize(...)`; keep the `Apply` fragment.
- In `Features/PaymentState.cs`, remove `IAuthorizePaymentState,`.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(payments): AuthorizePayment owns its state"
```

---

### Task 15: CapturePayment slice

Capture bounds the captured amount by the initiated amount, so this slice folds `Amount` — the only payment slice that does.

**Files:**
- Create: `apps/Alberto.Payments/Alberto.Payments/Features/CapturePayment/CapturePayment.cs`
- Create: `tests/Alberto.Examples.Tests/Payments/CapturePaymentTests.cs`
- Modify: `Features/PaymentMutations.cs`, `Features/Actions/Capture.cs`, `Features/PaymentState.cs`, `Contracts/PaymentInputs.cs`

**Interfaces:**
- Produces: `CapturePaymentInput`, `CapturePaymentState` (`PaymentId`, `Status`, `Amount`), `CapturePaymentEvolver`, `CapturePaymentDecider.Boundary(Guid)`, `CapturePaymentDecider.Decide(CapturePaymentState, decimal?, DateTimeOffset)`, `CapturePaymentMutation.CapturePayment(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Payments/CapturePaymentTests.cs`:

```csharp
using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class CapturePaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c002-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static CapturePaymentState Authorized()
    {
        var evolver = new CapturePaymentEvolver();
        var state = evolver.Apply(
            new CapturePaymentState(),
            new PaymentInitiated(PaymentId, Guid.NewGuid(), 100m, "EUR", "card"));

        return evolver.Apply(state, new PaymentAuthorized(PaymentId, "AUTH-1", Now));
    }

    [Fact]
    public void Captures_the_full_amount_when_none_is_given()
    {
        var decision = CapturePaymentDecider.Decide(Authorized(), capturedAmount: null, Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentCaptured>()
            .Which.CapturedAmount.Should().Be(100m);
    }

    [Fact]
    public void Refuses_to_capture_more_than_was_initiated()
    {
        var decision = CapturePaymentDecider.Decide(Authorized(), capturedAmount: 101m, Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.amount-out-of-range");
    }

    [Fact]
    public void Refuses_a_payment_that_was_never_authorized()
    {
        var state = new CapturePaymentEvolver().Apply(
            new CapturePaymentState(),
            new PaymentInitiated(PaymentId, Guid.NewGuid(), 100m, "EUR", "card"));

        var decision = CapturePaymentDecider.Decide(state, capturedAmount: null, Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.invalid-status");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~CapturePaymentTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Payments/Alberto.Payments/Features/CapturePayment/CapturePayment.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;

namespace Alberto.Payments.Features;

/// <summary>Input for capturing a payment.</summary>
public sealed record CapturePaymentInput(
    Guid PaymentId,
    decimal? Amount);

/// <summary>
/// Capture bounds the amount it takes by the amount initiated, so unlike the other payment
/// slices this one folds Amount.
/// </summary>
public sealed record CapturePaymentState
{
    public Guid PaymentId { get; init; }
    public PaymentStatus Status { get; init; } = PaymentStatus.None;
    public decimal Amount { get; init; }

    public bool Exists => PaymentId != Guid.Empty;
    public bool CanBeCaptured => Status == PaymentStatus.Authorized;
}

public sealed class CapturePaymentEvolver : Evolver<CapturePaymentState>,
    IEvolve<CapturePaymentState, PaymentInitiated>,
    IEvolve<CapturePaymentState, PaymentAuthorized>,
    IEvolve<CapturePaymentState, PaymentCaptured>,
    IEvolve<CapturePaymentState, PaymentFailed>,
    IEvolve<CapturePaymentState, PaymentRefunded>
{
    public CapturePaymentState Apply(CapturePaymentState s, PaymentInitiated e) =>
        s with { PaymentId = e.PaymentId, Amount = e.Amount, Status = PaymentStatus.Initiated };

    public CapturePaymentState Apply(CapturePaymentState s, PaymentAuthorized e) =>
        s with { Status = PaymentStatus.Authorized };

    public CapturePaymentState Apply(CapturePaymentState s, PaymentCaptured e) =>
        s with { Status = PaymentStatus.Captured };

    public CapturePaymentState Apply(CapturePaymentState s, PaymentFailed e) =>
        s with { Status = PaymentStatus.Failed };

    public CapturePaymentState Apply(CapturePaymentState s, PaymentRefunded e) =>
        s with { Status = PaymentStatus.Refunded };
}

public static class CapturePaymentDecider
{
    public static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    public static Decision Decide(
        CapturePaymentState state,
        decimal? capturedAmount,
        DateTimeOffset capturedAt)
    {
        if (!state.Exists)
            return Decision.Fail(PaymentProblems.NotFound());

        if (!state.CanBeCaptured)
            return Decision.Fail(PaymentProblems.InvalidStatus("captured", state.Status));

        var amountToCapture = capturedAmount ?? state.Amount;
        if (amountToCapture <= 0 || amountToCapture > state.Amount)
            return Decision.Fail(PaymentProblems.AmountOutOfRange("Captured", state.Amount));

        return Decision.Succeed(
            new PaymentCaptured(state.PaymentId, amountToCapture, capturedAt));
    }
}

public static class CapturePaymentMutation
{
    private const int ConflictRetries = 3;

    [Mutation]
    [GraphQLDescription("Captures a previously authorized payment.")]
    public static async Task<MutationResult> CapturePayment(
        CapturePaymentInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey)
            .Handle(input)
            .Load(cmd => CapturePaymentDecider.Boundary(cmd.PaymentId), new CapturePaymentEvolver())
            .Decide((cmd, state) =>
                CapturePaymentDecider.Decide(state, cmd.Amount, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
```

- [ ] **Step 4: Remove the old copies**

- Delete `CapturePayment` from `Features/PaymentMutations.cs`.
- In `Features/Actions/Capture.cs`, delete `ICapturePaymentState` and `static Decision Capture(...)`; keep the `Apply` fragment.
- In `Features/PaymentState.cs`, remove `ICapturePaymentState,`.
- In `Contracts/PaymentInputs.cs`, delete `CapturePaymentInput`.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(payments): CapturePayment owns its state"
```

---

### Task 16: FailPayment slice

**Files:**
- Create: `apps/Alberto.Payments/Alberto.Payments/Features/FailPayment/FailPayment.cs`
- Create: `tests/Alberto.Examples.Tests/Payments/FailPaymentTests.cs`
- Modify: `Features/PaymentMutations.cs`, `Features/Actions/Fail.cs`, `Features/PaymentState.cs`, `Contracts/PaymentInputs.cs`

**Interfaces:**
- Produces: `FailPaymentInput`, `FailPaymentState` (`PaymentId`, `Status`), `FailPaymentEvolver`, `FailPaymentDecider.Boundary(Guid)`, `FailPaymentDecider.Decide(FailPaymentState, string, string)`, `FailPaymentMutation.FailPayment(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Payments/FailPaymentTests.cs`:

```csharp
using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class FailPaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c003-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static FailPaymentState Initiated() =>
        new FailPaymentEvolver().Apply(
            new FailPaymentState(),
            new PaymentInitiated(PaymentId, Guid.NewGuid(), 10m, "EUR", "card"));

    [Fact]
    public void Fails_an_initiated_payment()
    {
        var decision = FailPaymentDecider.Decide(Initiated(), "declined", "Card declined");

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentFailed>();
    }

    [Fact]
    public void Fails_an_authorized_payment_too()
    {
        var state = new FailPaymentEvolver()
            .Apply(Initiated(), new PaymentAuthorized(PaymentId, "AUTH-1", Now));

        FailPaymentDecider.Decide(state, "reversed", "").IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Requires_an_error_code()
    {
        var decision = FailPaymentDecider.Decide(Initiated(), "", "Card declined");

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.error-code-required");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~FailPaymentTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Payments/Alberto.Payments/Features/FailPayment/FailPayment.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;

namespace Alberto.Payments.Features;

/// <summary>Input for failing a payment.</summary>
public sealed record FailPaymentInput(
    Guid PaymentId,
    string ErrorCode,
    string ErrorMessage);

/// <summary>
/// Failure is allowed from two statuses, and the error it records is its own — nothing about
/// an earlier failure's code or message affects the decision.
/// </summary>
public sealed record FailPaymentState
{
    public Guid PaymentId { get; init; }
    public PaymentStatus Status { get; init; } = PaymentStatus.None;

    public bool Exists => PaymentId != Guid.Empty;
    public bool CanBeFailed => Status is PaymentStatus.Initiated or PaymentStatus.Authorized;
}

public sealed class FailPaymentEvolver : Evolver<FailPaymentState>,
    IEvolve<FailPaymentState, PaymentInitiated>,
    IEvolve<FailPaymentState, PaymentAuthorized>,
    IEvolve<FailPaymentState, PaymentCaptured>,
    IEvolve<FailPaymentState, PaymentFailed>,
    IEvolve<FailPaymentState, PaymentRefunded>
{
    public FailPaymentState Apply(FailPaymentState s, PaymentInitiated e) =>
        s with { PaymentId = e.PaymentId, Status = PaymentStatus.Initiated };

    public FailPaymentState Apply(FailPaymentState s, PaymentAuthorized e) =>
        s with { Status = PaymentStatus.Authorized };

    public FailPaymentState Apply(FailPaymentState s, PaymentCaptured e) =>
        s with { Status = PaymentStatus.Captured };

    public FailPaymentState Apply(FailPaymentState s, PaymentFailed e) =>
        s with { Status = PaymentStatus.Failed };

    public FailPaymentState Apply(FailPaymentState s, PaymentRefunded e) =>
        s with { Status = PaymentStatus.Refunded };
}

public static class FailPaymentDecider
{
    public static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    public static Decision Decide(
        FailPaymentState state,
        string errorCode,
        string errorMessage)
    {
        if (!state.Exists)
            return Decision.Fail(PaymentProblems.NotFound());

        if (!state.CanBeFailed)
            return Decision.Fail(PaymentProblems.InvalidStatus("marked as failed", state.Status));

        if (string.IsNullOrWhiteSpace(errorCode))
            return Decision.Fail(PaymentProblems.ErrorCodeRequired());

        return Decision.Succeed(
            new PaymentFailed(state.PaymentId, errorCode, errorMessage ?? ""));
    }
}

public static class FailPaymentMutation
{
    private const int ConflictRetries = 3;

    [Mutation]
    [GraphQLDescription("Marks a payment as failed.")]
    public static async Task<MutationResult> FailPayment(
        FailPaymentInput input,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey)
            .Handle(input)
            .Load(cmd => FailPaymentDecider.Boundary(cmd.PaymentId), new FailPaymentEvolver())
            .Decide((cmd, state) =>
                FailPaymentDecider.Decide(state, cmd.ErrorCode, cmd.ErrorMessage))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
```

- [ ] **Step 4: Remove the old copies**

- Delete `FailPayment` from `Features/PaymentMutations.cs`.
- In `Features/Actions/Fail.cs`, delete `IFailPaymentState` and `static Decision Fail(...)`; keep the `Apply` fragment.
- In `Features/PaymentState.cs`, remove `IFailPaymentState,`.
- In `Contracts/PaymentInputs.cs`, delete `FailPaymentInput`.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(payments): FailPayment owns its state"
```

---

### Task 17: RefundPayment slice

The last write slice. After this, `PaymentMutations.cs` is empty and gets deleted.

**Files:**
- Create: `apps/Alberto.Payments/Alberto.Payments/Features/RefundPayment/RefundPayment.cs`
- Create: `tests/Alberto.Examples.Tests/Payments/RefundPaymentTests.cs`
- Delete: `apps/Alberto.Payments/Alberto.Payments/Features/PaymentMutations.cs`
- Modify: `Features/Actions/Refund.cs`, `Features/PaymentState.cs`, `Contracts/PaymentInputs.cs`

**Interfaces:**
- Produces: `RefundPaymentInput`, `RefundPaymentState` (`PaymentId`, `Status`, `CapturedAmount`), `RefundPaymentEvolver`, `RefundPaymentDecider.Boundary(Guid)`, `RefundPaymentDecider.Decide(RefundPaymentState, decimal, string, DateTimeOffset)`, `RefundPaymentMutation.RefundPayment(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Payments/RefundPaymentTests.cs`:

```csharp
using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class RefundPaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c004-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static RefundPaymentState Captured(decimal capturedAmount)
    {
        var evolver = new RefundPaymentEvolver();
        var state = evolver.Apply(
            new RefundPaymentState(),
            new PaymentInitiated(PaymentId, Guid.NewGuid(), 100m, "EUR", "card"));
        state = evolver.Apply(state, new PaymentAuthorized(PaymentId, "AUTH-1", Now));

        return evolver.Apply(state, new PaymentCaptured(PaymentId, capturedAmount, Now));
    }

    [Fact]
    public void Refunds_up_to_the_captured_amount()
    {
        var decision = RefundPaymentDecider.Decide(Captured(60m), 60m, "duplicate", Now);

        decision.IsSuccess.Should().BeTrue();
        decision.Events.Single().Should().BeOfType<PaymentRefunded>()
            .Which.RefundedAmount.Should().Be(60m);
    }

    [Fact]
    public void Refuses_more_than_was_captured_even_when_more_was_initiated()
    {
        var decision = RefundPaymentDecider.Decide(Captured(60m), 100m, "duplicate", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.amount-out-of-range");
    }

    [Fact]
    public void Refuses_a_payment_that_was_never_captured()
    {
        var evolver = new RefundPaymentEvolver();
        var state = evolver.Apply(
            new RefundPaymentState(),
            new PaymentInitiated(PaymentId, Guid.NewGuid(), 100m, "EUR", "card"));
        state = evolver.Apply(state, new PaymentAuthorized(PaymentId, "AUTH-1", Now));

        var decision = RefundPaymentDecider.Decide(state, 10m, "duplicate", Now);

        decision.IsError.Should().BeTrue();
        decision.Problems.Single().Code.Should().Be("payment.invalid-status");
    }
}
```

The second test is why `CapturedAmount` and `Amount` cannot be the same field: a partial capture caps the refund below the initiated amount.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~RefundPaymentTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Payments/Alberto.Payments/Features/RefundPayment/RefundPayment.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Examples.Shared;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;

namespace Alberto.Payments.Features;

/// <summary>Input for refunding a payment.</summary>
public sealed record RefundPaymentInput(
    Guid PaymentId,
    decimal Amount,
    string Reason);

/// <summary>
/// A refund is bounded by what was captured, not by what was initiated — a partial capture
/// caps the refund below the payment's amount, so this slice folds CapturedAmount and never
/// sees Amount.
/// </summary>
public sealed record RefundPaymentState
{
    public Guid PaymentId { get; init; }
    public PaymentStatus Status { get; init; } = PaymentStatus.None;
    public decimal? CapturedAmount { get; init; }

    public bool Exists => PaymentId != Guid.Empty;
    public bool CanBeRefunded => Status == PaymentStatus.Captured;
}

public sealed class RefundPaymentEvolver : Evolver<RefundPaymentState>,
    IEvolve<RefundPaymentState, PaymentInitiated>,
    IEvolve<RefundPaymentState, PaymentAuthorized>,
    IEvolve<RefundPaymentState, PaymentCaptured>,
    IEvolve<RefundPaymentState, PaymentFailed>,
    IEvolve<RefundPaymentState, PaymentRefunded>
{
    public RefundPaymentState Apply(RefundPaymentState s, PaymentInitiated e) =>
        s with { PaymentId = e.PaymentId, Status = PaymentStatus.Initiated };

    public RefundPaymentState Apply(RefundPaymentState s, PaymentAuthorized e) =>
        s with { Status = PaymentStatus.Authorized };

    public RefundPaymentState Apply(RefundPaymentState s, PaymentCaptured e) =>
        s with { Status = PaymentStatus.Captured, CapturedAmount = e.CapturedAmount };

    public RefundPaymentState Apply(RefundPaymentState s, PaymentFailed e) =>
        s with { Status = PaymentStatus.Failed };

    public RefundPaymentState Apply(RefundPaymentState s, PaymentRefunded e) =>
        s with { Status = PaymentStatus.Refunded };
}

public static class RefundPaymentDecider
{
    public static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    public static Decision Decide(
        RefundPaymentState state,
        decimal refundedAmount,
        string reason,
        DateTimeOffset refundedAt)
    {
        if (!state.Exists)
            return Decision.Fail(PaymentProblems.NotFound());

        if (!state.CanBeRefunded)
            return Decision.Fail(PaymentProblems.InvalidStatus("refunded", state.Status));

        var maxRefundable = state.CapturedAmount ?? 0;
        if (refundedAmount <= 0 || refundedAmount > maxRefundable)
            return Decision.Fail(PaymentProblems.AmountOutOfRange("Refund", maxRefundable));

        return Decision.Succeed(
            new PaymentRefunded(state.PaymentId, refundedAmount, reason ?? "", refundedAt));
    }
}

public static class RefundPaymentMutation
{
    private const int ConflictRetries = 3;

    [Mutation]
    [GraphQLDescription("Refunds a previously captured payment.")]
    public static async Task<MutationResult> RefundPayment(
        RefundPaymentInput input,
        [Service] IServiceProvider sp,
        [Service] TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await sp.GetRequiredKeyedService<AlbertoStore>(PaymentsModule.ModuleKey)
            .Handle(input)
            .Load(cmd => RefundPaymentDecider.Boundary(cmd.PaymentId), new RefundPaymentEvolver())
            .Decide((cmd, state) => RefundPaymentDecider.Decide(
                state, cmd.Amount, cmd.Reason, timeProvider.GetUtcNow()))
            .RetryOnConflict(ConflictRetries)
            .Commit(ct);

        result.EnsureCommitted();
        return new MutationResult();
    }
}
```

- [ ] **Step 4: Remove the old copies**

```bash
git rm apps/Alberto.Payments/Alberto.Payments/Features/PaymentMutations.cs
```

- In `Features/Actions/Refund.cs`, delete `IRefundPaymentState` and `static Decision Refund(...)`; keep the `Apply` fragment.
- In `Features/PaymentState.cs`, reduce the declaration to `public sealed record PaymentState`.
- In `Contracts/PaymentInputs.cs`, delete `RefundPaymentInput`. The file is now empty; delete it too.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS. All twelve write slices now own their state.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(payments): RefundPayment owns its state"
```

---

## Tasks 18–23: the read slices

The spec lists seven read slices, one per GraphQL field. These six tasks regroup them by **read model** instead: `getOrders` and `recentOrders` are two queries over the same EF projection, and a projection and the queries that read it change together — splitting them would put the entity's shape in one folder and its only consumers in two others. `GetOrder` and `GetPayment` keep a slice each because they fold the log rather than read a projection.

The resulting six read slices:

| Slice | Holds |
|---|---|
| `GetOrder` | log-folding read state, evolver, `getOrder` |
| `OrderSummaries` | `OrderSummaryEfProjection`, `Order`/`OrderItem`/`OrdersConnection`, `getOrders`, `recentOrders` |
| `OrdersOverview` | `OrdersOverviewProjection`, `OrdersOverview`, `ordersOverview` |
| `GetPayment` | log-folding read state, evolver, `getPayment` |
| `PaymentSummaries` | `PaymentSummaryProjection`, `PaymentSummary`, `Payment`, `recentPayments` |
| `PaymentsOverview` | `PaymentsOverviewProjection`, `PaymentsOverview`, `paymentsOverview` |

`OrderSummaryEntity` and `OrderLineItemData` stay in `Platform/` — EF migrations are generated from the `DbContext` and one already exists.

---

### Task 18: GetOrder read slice

`GetOrder` folds the log through `OrderEvolver` today. Its state is a read state: it needs every field the `Order` GraphQL type exposes, which is most of what `OrderState` had. That it looks like the old shared record is the point — this is one slice's state that happens to be wide, not a shared object that every slice pays for.

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Features/GetOrder/GetOrder.cs`
- Create: `tests/Alberto.Examples.Tests/Orders/GetOrderTests.cs`
- Modify: `apps/Alberto.Orders/Alberto.Orders/Features/OrderQueries.cs` (remove `GetOrder` and its two helpers)

**Interfaces:**
- Produces: `GetOrderState`, `GetOrderEvolver`, `GetOrderQuery.GetOrder(...)`.
- Consumes: `Order`, `OrderItem` from the `OrderSummaries` slice (Task 19). **Task 19 must land before this task builds** — write them in the order 19 → 18 if executing linearly, or accept a red build between the two.

To keep every task independently green, Task 18 runs **after** Task 19. The task numbering below reflects that: do 19 first.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Orders/GetOrderTests.cs`:

```csharp
using Alberto.Orders.Contracts;
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class GetOrderTests
{
    private static readonly Guid OrderId = Guid.Parse("0197b007-0000-7000-8000-000000000001");
    private static readonly Guid ProductId = Guid.Parse("0197b007-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Folds_the_whole_order_for_display()
    {
        var evolver = new GetOrderEvolver();
        var state = evolver.Apply(
            new GetOrderState(),
            new OrderCreated(OrderId, Guid.NewGuid(), [], "gift wrap"));
        state = evolver.Apply(state, new OrderItemAdded(OrderId, ProductId, "Widget", 2, 9.99m));
        state = evolver.Apply(state, new OrderConfirmed(OrderId, Now));
        state = evolver.Apply(state, new OrderShipped(OrderId, "TRACK-1", "DHL", Now));

        state.Exists.Should().BeTrue();
        state.Notes.Should().Be("gift wrap");
        state.Status.Should().Be(OrderStatus.Shipped);
        state.TrackingNumber.Should().Be("TRACK-1");
        state.Total.Should().Be(19.98m);
    }

    [Fact]
    public void Reports_an_order_it_has_never_seen_as_absent()
    {
        new GetOrderState().Exists.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~GetOrderTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Orders/Alberto.Orders/Features/GetOrder/GetOrder.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

/// <summary>
/// What <c>getOrder</c> shows. This is the widest state in the module because the field
/// exposes the whole order — but it is still one slice's state, folded by one slice's evolver,
/// and no decision depends on it.
/// </summary>
public sealed record GetOrderState
{
    public Guid OrderId { get; init; }
    public Guid CustomerId { get; init; }
    public IReadOnlyList<OrderLineItem> LineItems { get; init; } = [];
    public string? Notes { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.None;
    public string? TrackingNumber { get; init; }
    public string? Carrier { get; init; }
    public string? CancellationReason { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    public DateTimeOffset? ShippedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }

    public bool Exists => OrderId != Guid.Empty;
    public decimal Total => LineItems.Sum(x => x.Total);
}

public sealed class GetOrderEvolver : Evolver<GetOrderState>,
    IEvolve<GetOrderState, OrderCreated>,
    IEvolve<GetOrderState, OrderItemAdded>,
    IEvolve<GetOrderState, OrderItemRemoved>,
    IEvolve<GetOrderState, OrderConfirmed>,
    IEvolve<GetOrderState, OrderShipped>,
    IEvolve<GetOrderState, OrderDelivered>,
    IEvolve<GetOrderState, OrderCancelled>
{
    public GetOrderState Apply(GetOrderState s, OrderCreated e) => s with
    {
        OrderId = e.OrderId,
        CustomerId = e.CustomerId,
        LineItems = e.LineItems,
        Notes = e.Notes,
        Status = OrderStatus.Draft
    };

    public GetOrderState Apply(GetOrderState s, OrderItemAdded e) => s with
    {
        LineItems = s.LineItems
            .Where(x => x.ProductId != e.ProductId)
            .Append(new OrderLineItem(e.ProductId, e.ProductName, e.Quantity, e.UnitPrice))
            .ToList()
    };

    public GetOrderState Apply(GetOrderState s, OrderItemRemoved e) => s with
    {
        LineItems = s.LineItems.Where(x => x.ProductId != e.ProductId).ToList()
    };

    public GetOrderState Apply(GetOrderState s, OrderConfirmed e) =>
        s with { Status = OrderStatus.Confirmed, ConfirmedAt = e.ConfirmedAt };

    public GetOrderState Apply(GetOrderState s, OrderShipped e) => s with
    {
        Status = OrderStatus.Shipped,
        TrackingNumber = e.TrackingNumber,
        Carrier = e.Carrier,
        ShippedAt = e.ShippedAt
    };

    public GetOrderState Apply(GetOrderState s, OrderDelivered e) =>
        s with { Status = OrderStatus.Delivered, DeliveredAt = e.DeliveredAt };

    public GetOrderState Apply(GetOrderState s, OrderCancelled e) => s with
    {
        Status = OrderStatus.Cancelled,
        CancellationReason = e.Reason,
        CancelledAt = e.CancelledAt
    };
}

public static class GetOrderQuery
{
    private static DcbQuery Boundary(Guid orderId) => DcbQuery.For(Tags.Order, orderId);

    [Query]
    [GraphQLDescription("Gets an order by ID, rebuilt from events for consistency.")]
    public static async Task<Order?> GetOrder(
        Guid orderId,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(OrdersModule.ModuleKey);
        var events = await backend.StreamAsync(Boundary(orderId), cancellationToken: ct);
        var state = new GetOrderEvolver().Reconstitute(events);

        return state.Exists ? ToGraphQL(state) : null;
    }

    private static Order ToGraphQL(GetOrderState state) => new(
        state.OrderId,
        state.CustomerId,
        state.LineItems
            .Select(x => new OrderItem(x.ProductId, x.ProductName, x.Quantity, x.UnitPrice, x.Total))
            .ToList(),
        state.Notes,
        state.Status,
        state.Total,
        state.TrackingNumber,
        state.Carrier,
        state.CancellationReason,
        DateTimeOffset.MinValue, // Would need to track this in state
        state.ConfirmedAt,
        state.ShippedAt,
        state.DeliveredAt,
        state.CancelledAt,
        null);
}
```

The `DateTimeOffset.MinValue` for `CreatedAt` and the `null` for `UpdatedAt` are carried over verbatim from `OrderQueries.ToGraphQL`. Preserving them is deliberate: changing either would change what the field serves.

- [ ] **Step 4: Remove the old copies**

In `Features/OrderQueries.cs`, delete `GetOrder`, `LoadOrderState`, `ToGraphQL` and the `_evolver` field.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS, schema snapshot included.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(orders): GetOrder owns its read state"
```

---

### Task 19: OrderSummaries read slice

Do this **before** Task 18 — it owns the `Order` and `OrderItem` GraphQL types that Task 18 consumes.

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Features/OrderSummaries/OrderSummaries.cs`
- Create: `tests/Alberto.Examples.Tests/Orders/OrderSummariesTests.cs`
- Modify: `apps/Alberto.Orders/Alberto.Orders/Features/OrderQueries.cs` (remove `GetOrders`, `GetRecentOrders`)
- Modify: `apps/Alberto.Orders/Alberto.Orders/Contracts/Order.cs` (remove `Order`, `OrderItem`, `OrdersConnection`)
- Delete: `apps/Alberto.Orders/Alberto.Orders/Platform/Projections/OrderSummaryEfProjection.cs` (moves into the slice)
- Modify: `apps/Alberto.Orders/Alberto.Orders/Platform/OrdersModule.cs` (the `AddEfProjection` call now names the slice's declaration)

**Interfaces:**
- Produces: `Order`, `OrderItem`, `OrdersConnection`, `OrderSummaryEfProjection.Declaration`, `OrderSummariesQuery.GetOrders(...)`, `OrderSummariesQuery.GetRecentOrders(...)`.
- Consumes: `OrderSummaryEntity`, `OrderLineItemData`, `OrdersDbContext` from `Alberto.Orders.Platform`; `ITenantAccessor` from `Alberto.Dcb.Tenancy`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Orders/OrderSummariesTests.cs`:

```csharp
using Alberto.Orders.Features;
using Alberto.Orders.Platform;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class OrderSummariesTests
{
    [Fact]
    public void Connection_reports_more_pages_when_the_window_is_short_of_the_total()
    {
        var connection = new OrdersConnection([], TotalCount: 50, Skip: 0, Take: 20);

        connection.HasNextPage.Should().BeTrue();
        connection.HasPreviousPage.Should().BeFalse();
    }

    [Fact]
    public void Connection_reports_no_more_pages_on_the_last_window()
    {
        var connection = new OrdersConnection([], TotalCount: 50, Skip: 40, Take: 20);

        connection.HasNextPage.Should().BeFalse();
        connection.HasPreviousPage.Should().BeTrue();
    }

    [Fact]
    public void Projects_a_summary_entity_into_the_graphql_type()
    {
        var entity = new OrderSummaryEntity
        {
            OrderId = Guid.Parse("0197b008-0000-7000-8000-000000000001"),
            CustomerId = Guid.Parse("0197b008-0000-7000-8000-000000000002"),
            Total = 19.98m,
            LineItems =
            [
                new OrderLineItemData
                {
                    ProductId = Guid.Parse("0197b008-0000-7000-8000-000000000003"),
                    ProductName = "Widget",
                    Quantity = 2,
                    UnitPrice = 9.99m,
                    Total = 19.98m
                }
            ]
        };

        var order = Order.FromEntity(entity);

        order.OrderId.Should().Be(entity.OrderId);
        order.LineItems.Single().ProductName.Should().Be("Widget");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~OrderSummariesTests"
```

Expected: FAIL to compile — `OrdersConnection` and `Order` are not yet in `Alberto.Orders.Features`.

- [ ] **Step 3: Move the projection into the slice**

```bash
mkdir -p apps/Alberto.Orders/Alberto.Orders/Features/OrderSummaries
git mv apps/Alberto.Orders/Alberto.Orders/Platform/Projections/OrderSummaryEfProjection.cs \
       apps/Alberto.Orders/Alberto.Orders/Features/OrderSummaries/OrderSummaryEfProjection.cs
```

Change its namespace to `Alberto.Orders.Features` and its usings to `Alberto.Orders.Contracts` (events, `OrderStatus`) and `Alberto.Orders.Platform` (the entity types). Its body does not change.

In `Platform/OrdersModule.cs`, the `AddEfProjection<OrderSummaryEntity, OrdersDbContext>(OrderSummaryEfProjection.Declaration)` call now resolves `OrderSummaryEfProjection` from `Alberto.Orders.Features` — add that using.

- [ ] **Step 4: Write the query half of the slice**

`apps/Alberto.Orders/Alberto.Orders/Features/OrderSummaries/OrderSummaries.cs`:

```csharp
using Alberto.Dcb.Tenancy;
using Alberto.Orders.Contracts;
using Alberto.Orders.Platform;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Orders.Features;

/// <summary>GraphQL type for Order.</summary>
public sealed record Order(
    Guid OrderId,
    Guid CustomerId,
    IReadOnlyList<OrderItem> LineItems,
    string? Notes,
    OrderStatus Status,
    decimal Total,
    string? TrackingNumber,
    string? Carrier,
    string? CancellationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? UpdatedAt)
{
    public static Order FromEntity(OrderSummaryEntity e) => new(
        e.OrderId,
        e.CustomerId,
        e.LineItems.Select(OrderItem.FromEntity).ToList(),
        e.Notes,
        e.Status,
        e.Total,
        e.TrackingNumber,
        e.Carrier,
        e.CancellationReason,
        e.CreatedAt,
        e.ConfirmedAt,
        e.ShippedAt,
        e.DeliveredAt,
        e.CancelledAt,
        e.UpdatedAt);
}

/// <summary>GraphQL type for order line item.</summary>
public sealed record OrderItem(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal Total)
{
    public static OrderItem FromEntity(OrderLineItemData e) => new(
        e.ProductId,
        e.ProductName,
        e.Quantity,
        e.UnitPrice,
        e.Total);
}

/// <summary>Paginated connection for orders.</summary>
public sealed record OrdersConnection(
    IReadOnlyList<Order> Items,
    int TotalCount,
    int Skip,
    int Take)
{
    public bool HasNextPage => Skip + Take < TotalCount;
    public bool HasPreviousPage => Skip > 0;
}

public static class OrderSummariesQuery
{
    [Query]
    [GraphQLDescription("Gets orders with optional filtering by status, customer, and date range.")]
    public static async Task<OrdersConnection> GetOrders(
        [Service] IDbContextFactory<OrdersDbContext> contextFactory,
        [Service] ITenantAccessor tenant,
        OrderStatus? status = null,
        Guid? customerId = null,
        DateTimeOffset? createdAfter = null,
        DateTimeOffset? createdBefore = null,
        int skip = 0,
        int take = 20,
        CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var query = dbContext.OrderSummaries.Where(o => o.TenantId == tenantId);

        if (status.HasValue)
            query = query.Where(o => o.Status == status.Value);

        if (customerId.HasValue)
            query = query.Where(o => o.CustomerId == customerId.Value);

        if (createdAfter.HasValue)
            query = query.Where(o => o.CreatedAt >= createdAfter.Value);

        if (createdBefore.HasValue)
            query = query.Where(o => o.CreatedAt <= createdBefore.Value);

        var totalCount = await query.CountAsync(ct);

        var entities = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return new OrdersConnection(
            entities.Select(Order.FromEntity).ToList(),
            totalCount,
            skip,
            take);
    }

    [Query]
    [GraphQLDescription("Gets recent orders, ordered by creation date.")]
    public static async Task<IReadOnlyList<Order>> GetRecentOrders(
        [Service] IDbContextFactory<OrdersDbContext> contextFactory,
        [Service] ITenantAccessor tenant,
        int limit = 20,
        CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var entities = await dbContext.OrderSummaries
            .Where(o => o.TenantId == tenantId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(Order.FromEntity).ToList();
    }
}
```

`ITenantAccessor` replaces `GetTenantId(IResolverContext)`. Both read the tenant the interceptor set for the request — the interceptor writes it to `TenantContext` *and* to GraphQL global state — but only the accessor is something a module library can depend on without reaching into the host. Argument order matters for the schema: HotChocolate skips `[Service]` parameters when building the field, so `status`/`customerId`/… stay in the same order with the same defaults.

- [ ] **Step 5: Remove the old copies**

In `Features/OrderQueries.cs`, delete `GetOrders` and `GetRecentOrders`. In `Contracts/Order.cs`, delete `Order`, `OrderItem` and `OrdersConnection`.

- [ ] **Step 6: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS. `Schema_matches_snapshot` proves the two fields kept their arguments and defaults.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "refactor(orders): OrderSummaries owns its projection and queries"
```

---

### Task 20: OrdersOverview read slice

After this, `Features/OrderQueries.cs` is empty and gets deleted.

**Files:**
- Create: `apps/Alberto.Orders/Alberto.Orders/Features/OrdersOverview/OrdersOverview.cs`
- Create: `tests/Alberto.Examples.Tests/Orders/OrdersOverviewTests.cs`
- Delete: `apps/Alberto.Orders/Alberto.Orders/Platform/Projections/OrdersOverviewProjection.cs`, `Platform/ReadModels/OrdersOverview.cs`, `Features/OrderQueries.cs`
- Modify: `Platform/OrdersModule.cs`

**Interfaces:**
- Produces: `OrdersOverview` (read model), `OrdersOverviewProjection.Declaration`, `OrdersOverviewProjection.DocumentId`, `OrdersOverviewProjection.StateStore(...)`, `OrdersOverviewQuery.GetOrdersOverview(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Orders/OrdersOverviewTests.cs`:

```csharp
using Alberto.Orders.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Orders;

public sealed class OrdersOverviewTests
{
    [Fact]
    public void Declaration_handles_every_status_changing_event()
    {
        OrdersOverviewProjection.Declaration.Name
            .Should().Be(nameof(OrdersOverviewProjection));
    }

    [Fact]
    public void Overview_starts_empty()
    {
        var overview = new OrdersOverview();

        overview.TotalOrders.Should().Be(0);
        overview.TotalRevenue.Should().Be(0);
    }
}
```

Confirm `ProjectionDeclaration<T>`'s name property before writing the first assertion; if it is not `Name`, assert on whatever identifies the declaration.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~OrdersOverviewTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Move the projection and read model into the slice**

```bash
mkdir -p apps/Alberto.Orders/Alberto.Orders/Features/OrdersOverview
git mv apps/Alberto.Orders/Alberto.Orders/Platform/Projections/OrdersOverviewProjection.cs \
       apps/Alberto.Orders/Alberto.Orders/Features/OrdersOverview/OrdersOverviewProjection.cs
git mv apps/Alberto.Orders/Alberto.Orders/Platform/ReadModels/OrdersOverview.cs \
       apps/Alberto.Orders/Alberto.Orders/Features/OrdersOverview/OrdersOverview.cs
```

Set both namespaces to `Alberto.Orders.Features`; the projection's using becomes `Alberto.Orders.Contracts`. Bodies unchanged.

- [ ] **Step 4: Give the slice the store factory and the query**

Append to `Features/OrdersOverview/OrdersOverviewProjection.cs`:

```csharp
    /// <summary>
    /// Builds the state store this projection writes and <c>ordersOverview</c> reads, so the
    /// two cannot disagree about schema, projection name, rebuild version or tenancy.
    /// </summary>
    public static Func<string?, IStateStore<OrdersOverview>> StateStore(ProjectionStoreContext ctx)
    {
        var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(OrdersModule.ModuleKey);
        return tenantId => new PostgresStateStore<OrdersOverview>(
            dataSource,
            nameof(OrdersOverviewProjection),
            "orders",
            rebuildVersion: ctx.RebuildVersion,
            tenantId: TenantScope.CrossTenantFor(tenantId));
    }
```

Check the delegate's real parameter type in `AddProjection`'s signature (`src/Alberto.Dcb/DcbModuleBuilderExtensions.cs`) and use that type name rather than the placeholder `ProjectionStoreContext` if it differs.

`apps/Alberto.Orders/Alberto.Orders/Features/OrdersOverview/OrdersOverviewQuery.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Tenancy;
using Alberto.Orders.Platform;

namespace Alberto.Orders.Features;

public static class OrdersOverviewQuery
{
    [Query]
    [GraphQLDescription("Gets aggregated order statistics from the async projection.")]
    public static async Task<OrdersOverview?> GetOrdersOverview(
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        // A cross-tenant aggregate: the control loop blends every tenant's events into one
        // document under TenantScope.CrossTenant. The factory resolved here is the writer's
        // own, so the only thing this resolver decides is which tenant to read — and passing
        // the request's tenant would be wrong, not merely empty.
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<OrdersOverview>>>(
            $"{OrdersModule.ModuleKey}:{nameof(OrdersOverviewProjection)}");

        var states = await factory(TenantScope.CrossTenant)
            .LoadManyAsync([OrdersOverviewProjection.DocumentId], ct: ct);

        return states.GetValueOrDefault(OrdersOverviewProjection.DocumentId);
    }
}
```

In `Platform/OrdersModule.cs`, replace the inline lambda with the slice's factory:

```csharp
            .AddProjection(OrdersOverviewProjection.Declaration, OrdersOverviewProjection.StateStore)
```

- [ ] **Step 5: Remove the old copy**

```bash
git rm apps/Alberto.Orders/Alberto.Orders/Features/OrderQueries.cs
```

It is empty at this point — `GetOrder`, `GetOrders`, `GetRecentOrders` and `GetOrdersOverview` have all moved.

- [ ] **Step 6: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "refactor(orders): OrdersOverview owns its projection and query"
```

---

### Task 21: GetPayment read slice

`GetPayment` currently hand-rolls a `JsonSerializer.Deserialize` switch over event type ids instead of using an evolver. The slice replaces that with a real `Evolver<GetPaymentState>` — same output, and it stops the read path from duplicating the dispatch table.

**Files:**
- Create: `apps/Alberto.Payments/Alberto.Payments/Features/GetPayment/GetPayment.cs`
- Create: `tests/Alberto.Examples.Tests/Payments/GetPaymentTests.cs`
- Modify: `apps/Alberto.Payments/Alberto.Payments/Features/PaymentQueries.cs`

**Interfaces:**
- Produces: `GetPaymentState`, `GetPaymentEvolver`, `GetPaymentQuery.GetPayment(...)`.
- Consumes: `Payment` from the `PaymentSummaries` slice (Task 22) — **do Task 22 first**.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Payments/GetPaymentTests.cs`:

```csharp
using Alberto.Payments.Contracts;
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class GetPaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("0197c005-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Folds_the_whole_payment_for_display()
    {
        var evolver = new GetPaymentEvolver();
        var state = evolver.Apply(
            new GetPaymentState(),
            new PaymentInitiated(PaymentId, Guid.NewGuid(), 100m, "EUR", "card"));
        state = evolver.Apply(state, new PaymentAuthorized(PaymentId, "AUTH-1", Now));
        state = evolver.Apply(state, new PaymentCaptured(PaymentId, 100m, Now));
        state = evolver.Apply(state, new PaymentRefunded(PaymentId, 40m, "partial", Now));

        state.Exists.Should().BeTrue();
        state.Currency.Should().Be("EUR");
        state.AuthorizationCode.Should().Be("AUTH-1");
        state.RefundedAmount.Should().Be(40m);
        state.Status.Should().Be(PaymentStatus.Refunded);
    }

    [Fact]
    public void Reports_a_payment_it_has_never_seen_as_absent()
    {
        new GetPaymentState().Exists.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~GetPaymentTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Write the slice**

`apps/Alberto.Payments/Alberto.Payments/Features/GetPayment/GetPayment.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Payments.Contracts;
using Alberto.Payments.Platform;

namespace Alberto.Payments.Features;

/// <summary>What <c>getPayment</c> shows, folded straight from the log.</summary>
public sealed record GetPaymentState
{
    public Guid PaymentId { get; init; }
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = string.Empty;
    public PaymentStatus Status { get; init; } = PaymentStatus.None;
    public string? AuthorizationCode { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public decimal? RefundedAmount { get; init; }
    public DateTimeOffset? AuthorizedAt { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public DateTimeOffset? RefundedAt { get; init; }

    public bool Exists => PaymentId != Guid.Empty;
}

/// <remarks>
/// This replaces a hand-written switch over event-type ids in <c>PaymentQueries</c> that
/// deserialized each event itself. An evolver gets the same fold from the dispatch table the
/// framework already builds, and cannot drift from the event types the way a literal
/// <c>"payment-captured"</c> string can.
/// </remarks>
public sealed class GetPaymentEvolver : Evolver<GetPaymentState>,
    IEvolve<GetPaymentState, PaymentInitiated>,
    IEvolve<GetPaymentState, PaymentAuthorized>,
    IEvolve<GetPaymentState, PaymentCaptured>,
    IEvolve<GetPaymentState, PaymentFailed>,
    IEvolve<GetPaymentState, PaymentRefunded>
{
    public GetPaymentState Apply(GetPaymentState s, PaymentInitiated e) => s with
    {
        PaymentId = e.PaymentId,
        OrderId = e.OrderId,
        Amount = e.Amount,
        Currency = e.Currency,
        PaymentMethod = e.PaymentMethod,
        Status = PaymentStatus.Initiated
    };

    public GetPaymentState Apply(GetPaymentState s, PaymentAuthorized e) => s with
    {
        AuthorizationCode = e.AuthorizationCode,
        AuthorizedAt = e.AuthorizedAt,
        Status = PaymentStatus.Authorized
    };

    public GetPaymentState Apply(GetPaymentState s, PaymentCaptured e) => s with
    {
        CapturedAt = e.CapturedAt,
        Status = PaymentStatus.Captured
    };

    public GetPaymentState Apply(GetPaymentState s, PaymentFailed e) => s with
    {
        ErrorCode = e.ErrorCode,
        ErrorMessage = e.ErrorMessage,
        Status = PaymentStatus.Failed
    };

    public GetPaymentState Apply(GetPaymentState s, PaymentRefunded e) => s with
    {
        RefundedAmount = e.RefundedAmount,
        RefundedAt = e.RefundedAt,
        Status = PaymentStatus.Refunded
    };
}

public static class GetPaymentQuery
{
    private static DcbQuery Boundary(Guid paymentId) => DcbQuery.For(Tags.Payment, paymentId);

    [Query]
    [GraphQLDescription("Gets a payment by ID, rebuilt from events for consistency.")]
    public static async Task<Payment?> GetPayment(
        Guid paymentId,
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(PaymentsModule.ModuleKey);
        var events = await backend.StreamAsync(Boundary(paymentId), cancellationToken: ct);
        var state = new GetPaymentEvolver().Reconstitute(events);

        return state.Exists ? ToGraphQL(state) : null;
    }

    private static Payment ToGraphQL(GetPaymentState state) => new(
        state.PaymentId,
        state.OrderId,
        state.Amount,
        state.Currency,
        state.PaymentMethod,
        state.Status,
        state.AuthorizationCode,
        state.ErrorCode,
        state.ErrorMessage,
        state.RefundedAmount,
        DateTimeOffset.MinValue, // Would need to track CreatedAt in state
        state.AuthorizedAt,
        state.CapturedAt,
        state.RefundedAt);
}
```

- [ ] **Step 4: Remove the old copies**

In `Features/PaymentQueries.cs`, delete `GetPayment`, `LoadPaymentState`, `ToGraphQL` and the now-unused `System.Text.Json` using.

- [ ] **Step 5: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "refactor(payments): GetPayment owns its read state"
```

---

### Task 22: PaymentSummaries read slice

Do this **before** Task 21 — it owns the `Payment` GraphQL type.

**Files:**
- Create: `apps/Alberto.Payments/Alberto.Payments/Features/PaymentSummaries/PaymentSummaries.cs`
- Create: `tests/Alberto.Examples.Tests/Payments/PaymentSummariesTests.cs`
- Move: `Platform/Projections/PaymentSummaryProjection.cs`, `Platform/ReadModels/PaymentSummary.cs` into the slice
- Modify: `Features/PaymentQueries.cs`, `Contracts/Payment.cs`, `Platform/PaymentsModule.cs`

**Interfaces:**
- Produces: `Payment`, `PaymentSummary`, `PaymentStatus` (read-model enum), `PaymentSummaryProjection.Declaration`, `PaymentSummaryProjection.StateStore(...)`, `PaymentSummariesQuery.GetRecentPayments(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Payments/PaymentSummariesTests.cs`:

```csharp
using Alberto.Payments.Features;
using FluentAssertions;
using CorePaymentStatus = Alberto.Payments.Contracts.PaymentStatus;

namespace Alberto.Examples.Tests.Payments;

public sealed class PaymentSummariesTests
{
    [Fact]
    public void Maps_the_read_model_status_by_name_not_by_ordinal()
    {
        var summary = new PaymentSummary
        {
            PaymentId = Guid.Parse("0197c006-0000-7000-8000-000000000001"),
            OrderId = Guid.Parse("0197c006-0000-7000-8000-000000000002"),
            Amount = 100m,
            Currency = "EUR",
            PaymentMethod = "card",
            Status = PaymentStatus.Initiated
        };

        Payment.FromSummary(summary).Status.Should().Be(CorePaymentStatus.Initiated);
    }

    [Fact]
    public void Maps_a_captured_payment_to_captured()
    {
        var summary = new PaymentSummary
        {
            PaymentId = Guid.NewGuid(),
            Status = PaymentStatus.Captured
        };

        Payment.FromSummary(summary).Status.Should().Be(CorePaymentStatus.Captured);
    }
}
```

The two enums have different ordinals — the read model starts at `Initiated = 0`, the contract at `None = 0`. These tests pin the by-name mapping that fixed a past silent shift.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~PaymentSummariesTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Move the projection and read model into the slice**

```bash
mkdir -p apps/Alberto.Payments/Alberto.Payments/Features/PaymentSummaries
git mv apps/Alberto.Payments/Alberto.Payments/Platform/Projections/PaymentSummaryProjection.cs \
       apps/Alberto.Payments/Alberto.Payments/Features/PaymentSummaries/PaymentSummaryProjection.cs
git mv apps/Alberto.Payments/Alberto.Payments/Platform/ReadModels/PaymentSummary.cs \
       apps/Alberto.Payments/Alberto.Payments/Features/PaymentSummaries/PaymentSummary.cs
```

Set both namespaces to `Alberto.Payments.Features`. `PaymentSummary.cs` carries the read model's own `PaymentStatus` enum — it moves with it.

- [ ] **Step 4: Write the query half and the store factory**

Append to `Features/PaymentSummaries/PaymentSummaryProjection.cs`:

```csharp
    /// <summary>
    /// One document per payment, so its documents belong to individual tenants: the store takes
    /// the tenant of the events it is given, and a reader gets back only its own tenant's rows.
    /// </summary>
    public static Func<string?, IStateStore<PaymentSummary>> StateStore(ProjectionStoreContext ctx)
    {
        var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(PaymentsModule.ModuleKey);
        return tenantId => new PostgresStateStore<PaymentSummary>(
            dataSource,
            nameof(PaymentSummaryProjection),
            "payments",
            rebuildVersion: ctx.RebuildVersion,
            tenantId: tenantId);
    }
```

`apps/Alberto.Payments/Alberto.Payments/Features/PaymentSummaries/PaymentSummaries.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Tenancy;
using Alberto.Payments.Platform;
using CorePaymentStatus = Alberto.Payments.Contracts.PaymentStatus;

namespace Alberto.Payments.Features;

/// <summary>GraphQL type for Payment.</summary>
public sealed record Payment(
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string PaymentMethod,
    CorePaymentStatus Status,
    string? AuthorizationCode,
    string? ErrorCode,
    string? ErrorMessage,
    decimal? RefundedAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? CapturedAt,
    DateTimeOffset? RefundedAt)
{
    public static Payment FromSummary(PaymentSummary s) => new(
        s.PaymentId,
        s.OrderId,
        s.Amount,
        s.Currency,
        s.PaymentMethod,
        ToContractStatus(s.Status),
        s.AuthorizationCode,
        s.ErrorCode,
        s.ErrorMessage,
        s.RefundedAmount,
        s.CreatedAt,
        s.AuthorizedAt,
        s.CapturedAt,
        s.RefundedAt);

    /// <summary>
    /// Maps the read model's status onto the one this GraphQL type exposes.
    /// </summary>
    /// <remarks>
    /// These are two independent enums that happen to share member names, and their ordinals do
    /// not line up: the contract one starts at <c>None = 0</c>, the read model's at
    /// <c>Initiated = 0</c>. The numeric cast this replaced therefore shifted every payment one
    /// rung down the ladder — an initiated payment reported <c>NONE</c>, a captured one
    /// <c>AUTHORIZED</c>, a refunded one <c>FAILED</c>.
    /// <para>
    /// Naming both sides makes a future member added to either enum a compile error here rather
    /// than another silent shift.
    /// </para>
    /// </remarks>
    private static CorePaymentStatus ToContractStatus(PaymentStatus status) => status switch
    {
        PaymentStatus.Initiated => CorePaymentStatus.Initiated,
        PaymentStatus.Authorized => CorePaymentStatus.Authorized,
        PaymentStatus.Captured => CorePaymentStatus.Captured,
        PaymentStatus.Failed => CorePaymentStatus.Failed,
        PaymentStatus.Refunded => CorePaymentStatus.Refunded,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unmapped payment status from the read model."),
    };
}

public static class PaymentSummariesQuery
{
    [Query]
    [GraphQLDescription("Gets the calling tenant's recent payments, ordered by last update.")]
    public static async Task<IReadOnlyList<Payment>> GetRecentPayments(
        [Service] IServiceProvider sp,
        [Service] ITenantAccessor tenant,
        int limit = 20,
        CancellationToken ct = default)
    {
        // Not an aggregate: one document per PaymentId, so its documents belong to individual
        // tenants and this field must read under one. Resolving the writer's own factory is
        // what keeps reader and writer agreeing on the primary key.
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<PaymentSummary>>>(
            $"{PaymentsModule.ModuleKey}:{nameof(PaymentSummaryProjection)}");

        var summaries = await factory(tenant.TenantId).ListRecentAsync(limit, ct);
        return summaries.Select(Payment.FromSummary).ToList();
    }
}
```

In `Platform/PaymentsModule.cs`, replace the `PaymentSummaryProjection` lambda with `PaymentSummaryProjection.StateStore`.

- [ ] **Step 5: Remove the old copies**

In `Features/PaymentQueries.cs`, delete `GetRecentPayments` and the `ReaderFor<TState>` helper. In `Contracts/Payment.cs`, delete the `Payment` record.

- [ ] **Step 6: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "refactor(payments): PaymentSummaries owns its projection and query"
```

---

### Task 23: PaymentsOverview read slice

The last read slice. After this, `Features/PaymentQueries.cs` is empty and gets deleted.

**Files:**
- Create: `apps/Alberto.Payments/Alberto.Payments/Features/PaymentsOverview/PaymentsOverviewQuery.cs`
- Create: `tests/Alberto.Examples.Tests/Payments/PaymentsOverviewTests.cs`
- Move: `Platform/Projections/PaymentsOverviewProjection.cs`, `Platform/ReadModels/PaymentsOverview.cs` into the slice
- Delete: `Features/PaymentQueries.cs`
- Modify: `Platform/PaymentsModule.cs`

**Interfaces:**
- Produces: `PaymentsOverview`, `PaymentsOverviewProjection.Declaration`, `PaymentsOverviewProjection.DocumentId`, `PaymentsOverviewProjection.StateStore(...)`, `PaymentsOverviewQuery.GetPaymentsOverview(...)`.

- [ ] **Step 1: Write the failing test**

`tests/Alberto.Examples.Tests/Payments/PaymentsOverviewTests.cs`:

```csharp
using Alberto.Payments.Features;
using FluentAssertions;

namespace Alberto.Examples.Tests.Payments;

public sealed class PaymentsOverviewTests
{
    [Fact]
    public void Every_event_lands_on_one_document()
    {
        PaymentsOverviewProjection.DocumentId.Should().Be("overview");
    }

    [Fact]
    public void Overview_starts_empty()
    {
        var overview = new PaymentsOverview();

        overview.TotalPayments.Should().Be(0);
        overview.TotalCapturedAmount.Should().Be(0);
    }
}
```

`DocumentId` is `private const string Overview = "overview"` today. The query hard-codes the same literal in a second place; making it a public constant on the projection removes the duplication and is what the test pins.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Alberto.Examples.Tests --filter "FullyQualifiedName~PaymentsOverviewTests"
```

Expected: FAIL to compile.

- [ ] **Step 3: Move the projection and read model into the slice**

```bash
mkdir -p apps/Alberto.Payments/Alberto.Payments/Features/PaymentsOverview
git mv apps/Alberto.Payments/Alberto.Payments/Platform/Projections/PaymentsOverviewProjection.cs \
       apps/Alberto.Payments/Alberto.Payments/Features/PaymentsOverview/PaymentsOverviewProjection.cs
git mv apps/Alberto.Payments/Alberto.Payments/Platform/ReadModels/PaymentsOverview.cs \
       apps/Alberto.Payments/Alberto.Payments/Features/PaymentsOverview/PaymentsOverview.cs
```

Set both namespaces to `Alberto.Payments.Features`, rename the projection's `private const string Overview` to:

```csharp
    /// <summary>The single document this projection maintains.</summary>
    public const string DocumentId = "overview";
```

and update its five `id: _ => Overview` selectors to `id: _ => DocumentId`. Append the store factory:

```csharp
    /// <summary>
    /// A single running total blended across every tenant, so it is stored under
    /// TenantScope.CrossTenant rather than under any one of them.
    /// </summary>
    public static Func<string?, IStateStore<PaymentsOverview>> StateStore(ProjectionStoreContext ctx)
    {
        var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(PaymentsModule.ModuleKey);
        return tenantId => new PostgresStateStore<PaymentsOverview>(
            dataSource,
            nameof(PaymentsOverviewProjection),
            "payments",
            rebuildVersion: ctx.RebuildVersion,
            tenantId: TenantScope.CrossTenantFor(tenantId));
    }
```

- [ ] **Step 4: Write the query**

`apps/Alberto.Payments/Alberto.Payments/Features/PaymentsOverview/PaymentsOverviewQuery.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Tenancy;
using Alberto.Payments.Platform;

namespace Alberto.Payments.Features;

public static class PaymentsOverviewQuery
{
    [Query]
    [GraphQLDescription("Gets aggregated payment statistics from the async projection.")]
    public static async Task<PaymentsOverview?> GetPaymentsOverview(
        [Service] IServiceProvider sp,
        CancellationToken ct)
    {
        // Blends every tenant into one document under TenantScope.CrossTenant. Reading it with
        // the request's tenant would look correct and return nothing.
        var factory = sp.GetRequiredKeyedService<Func<string?, IStateStore<PaymentsOverview>>>(
            $"{PaymentsModule.ModuleKey}:{nameof(PaymentsOverviewProjection)}");

        var states = await factory(TenantScope.CrossTenant)
            .LoadManyAsync([PaymentsOverviewProjection.DocumentId], ct: ct);

        return states.GetValueOrDefault(PaymentsOverviewProjection.DocumentId);
    }
}
```

In `Platform/PaymentsModule.cs`, replace the `PaymentsOverviewProjection` lambda with `PaymentsOverviewProjection.StateStore`. The module's `AddProjection` calls are now two one-liners.

- [ ] **Step 5: Remove the old copy**

```bash
git rm apps/Alberto.Payments/Alberto.Payments/Features/PaymentQueries.cs
```

- [ ] **Step 6: Run the tests**

```bash
dotnet build && dotnet test tests/Alberto.Examples.Tests
```

Expected: all PASS. Every GraphQL field now lives in a slice folder.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "refactor(payments): PaymentsOverview owns its projection and query"
```

---

### Task 24: Delete the shared state

The old shared records are now referenced only by their own evolvers and the `Apply` fragments kept alive for them. This task removes all of it and proves nothing pointed at it.

**Files:**
- Delete: `apps/Alberto.Orders/Alberto.Orders/Features/OrderState.cs`, `Features/OrderEvolver.cs`, `Features/OrderDecider.cs`, `Features/Actions/` (7 files)
- Delete: `apps/Alberto.Payments/Alberto.Payments/Features/PaymentState.cs`, `Features/PaymentEvolver.cs`, `Features/PaymentDecider.cs`, `Features/Actions/` (5 files)
- Modify: `apps/Alberto.Orders/Alberto.Orders/Contracts/OrderStatus.cs` — hosts `OrderStatus`, which `OrderState.cs` currently declares
- Modify: `apps/Alberto.Payments/Alberto.Payments/Contracts/PaymentStatus.cs` — same for `PaymentStatus`

**Interfaces:**
- Produces: nothing new. Removes `OrderState`, `OrderEvolver`, `OrderDecider`, `ICreateOrderState`…`ICancelOrderState`, `PaymentState`, `PaymentEvolver`, `PaymentDecider`, `IInitiatePaymentState`…`IRefundPaymentState`.

- [ ] **Step 1: Move the two status enums out of the state files**

`OrderStatus` is declared at the bottom of `Features/OrderState.cs` and `PaymentStatus` at the bottom of `Features/PaymentState.cs`. Both are contract types — every slice, both projections, the EF entity and the GraphQL schema use them. Move them to `Contracts/OrderStatus.cs` and `Contracts/PaymentStatus.cs`, in namespaces `Alberto.Orders.Contracts` and `Alberto.Payments.Contracts`, with their members and explicit values unchanged:

```csharp
namespace Alberto.Orders.Contracts;

/// <summary>Possible states of an order.</summary>
public enum OrderStatus
{
    None = 0,
    Draft = 1,
    Confirmed = 2,
    Shipped = 3,
    Delivered = 4,
    Cancelled = 5
}
```

```csharp
namespace Alberto.Payments.Contracts;

/// <summary>Possible states of a payment.</summary>
public enum PaymentStatus
{
    None = 0,
    Initiated = 1,
    Authorized = 2,
    Captured = 3,
    Failed = 4,
    Refunded = 5
}
```

The explicit values matter: `OrderStatus` is persisted by the EF projection with `.HasConversion<string>()`, so a name change would break stored rows, and the GraphQL enum members come from these names.

- [ ] **Step 2: Delete the shared state**

```bash
git rm apps/Alberto.Orders/Alberto.Orders/Features/OrderState.cs \
       apps/Alberto.Orders/Alberto.Orders/Features/OrderEvolver.cs \
       apps/Alberto.Orders/Alberto.Orders/Features/OrderDecider.cs
git rm -r apps/Alberto.Orders/Alberto.Orders/Features/Actions
git rm apps/Alberto.Payments/Alberto.Payments/Features/PaymentState.cs \
       apps/Alberto.Payments/Alberto.Payments/Features/PaymentEvolver.cs \
       apps/Alberto.Payments/Alberto.Payments/Features/PaymentDecider.cs
git rm -r apps/Alberto.Payments/Alberto.Payments/Features/Actions
```

- [ ] **Step 3: Build**

```bash
dotnet build
```

Expected: success. Anything still referencing the deleted types is a compile error naming the file — fix it by pointing at the owning slice, never by reinstating a shared type.

- [ ] **Step 4: Prove nothing references the shared state**

```bash
grep -rn "OrderState\b\|PaymentState\b\|OrderEvolver\|PaymentEvolver\|OrderDecider\|PaymentDecider" apps tests --include='*.cs' | grep -v "/obj/"
```

Expected: only per-slice names matched by the prefix (`CreateOrderState`, `ShipOrderEvolver`, `RefundPaymentDecider`, …). No bare `OrderState`, `PaymentState`, `OrderEvolver`, `PaymentEvolver`, `OrderDecider` or `PaymentDecider`.

- [ ] **Step 5: Prove no slice reaches into another**

```bash
grep -rn "Features\.[A-Z]" apps --include='*.cs' | grep -v "/obj/"
```

Expected: no output. Every slice type sits in the flat `Alberto.Orders.Features` / `Alberto.Payments.Features` namespace, so a cross-slice reference would not show as a qualified name — the real check is the next one.

```bash
grep -rn "State\b" apps/Alberto.Orders/Alberto.Orders/Features --include='*.cs' \
  | grep -v "/obj/" | awk -F/ '{print $6}' | sort -u
```

Expected: each state type name appears only under its own slice folder. A slice folder that mentions another slice's state is the failure this refactor exists to prevent.

- [ ] **Step 6: Run the tests**

```bash
dotnet test
```

Expected: all PASS, including the existing `Alberto.Dcb.Tests` suite.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "refactor: delete the shared order and payment state"
```

---

### Task 25: Update the documentation

**Files:**
- Modify: `CLAUDE.md`
- Create: `docs/architecture/vertical-slices.md`
- Modify: `docs/architecture/async-processing.md` (only if it names moved types)

**Interfaces:**
- Produces: nothing in code.

- [ ] **Step 1: Find every stale path**

```bash
grep -rn "Alberto.Orders.Core\|Alberto.Orders.Infrastructure\|Alberto.Payments.Core\|Alberto.Payments.Infrastructure" \
  --include='*.md' . | grep -v node_modules
```

Every hit is a path that no longer exists.

- [ ] **Step 2: Rewrite the directory tree in `CLAUDE.md`**

Replace the `/apps/` block of the Directory Structure section with:

```
/apps/                          # Example applications
  Alberto.AppHost/              # Aspire orchestration
  Alberto.Examples.Shared/      # MutationResult and the Result→GraphQL error mapping
  ServiceDefaults/              # Shared Aspire service configuration
  Alberto.Orders/               # Orders example
    Alberto.Orders/             # The module: Contracts/, Features/ (one folder per slice), Platform/
    Alberto.Orders.Api/         # Host: Program.cs, tenant interceptor
    Alberto.Orders.Migrations/  # EF migrations runner
  Alberto.Payments/             # Payments example
    Alberto.Payments/           # The module, same shape as Orders
```

and update the note below it: Payments is in the solution and builds, its slices are registered by the Orders API host, and it has no host of its own.

- [ ] **Step 3: Add a Key Patterns entry**

Add to the Key Patterns list in `CLAUDE.md`:

```markdown
- **Vertical slices in the examples**: `apps/Alberto.Orders` and `apps/Alberto.Payments` are sliced by behaviour, not layer. One folder per slice under `Features/`, holding that slice's input type, state record, evolver, decision function, boundary and GraphQL operation. **Slices share the event log and nothing else** — no shared state record, no shared evolver, no base state. `Contracts/` (events, status enums, problem codes, tag keys) and `Platform/` (DI, `DbContext`, EF migrations) are the two deliberate exceptions, named so they cannot be mistaken for domain code that happens to be shared. Five slices fold `OrderCreated`, each projecting a different part of it; that duplication is the pattern working. See [docs/architecture/vertical-slices.md](docs/architecture/vertical-slices.md)
```

- [ ] **Step 4: Write `docs/architecture/vertical-slices.md`**

Cover, in this order:

1. **What a slice owns** — the six things in one file, with `ShipOrder.cs` quoted as the reference.
2. **Why state is not shared** — `ShipOrderState` has two properties; the record it replaced had twelve, and every action carried all of them. Point at `RefundPaymentState.CapturedAmount` vs `CapturePaymentState.Amount` as two slices that would have been forced into one field.
3. **What is global, and why each one is** — events (the log is the only shared thing), the status enums (persisted by name and exposed in the schema), problem codes (client contract), tag keys (boundaries are built from them).
4. **The duplication rule** — a shared `ApplyCreated` or a base state record reintroduces exactly what this removed. If two slices want the same helper, they get two copies.
5. **Boundaries** — every slice keeps `DcbQuery.For(Tags.Order, orderId)`. Narrowing the type axis is a per-slice argument with a per-slice justification, on one line, in the slice.
6. **Read slices** — grouped by read model rather than by field, because a projection and every query over it change together.

- [ ] **Step 5: Check the async-processing doc**

```bash
grep -n "OrdersOverviewProjection\|OrderSummaryEfProjection\|Infrastructure" docs/architecture/async-processing.md
```

Fix any path or namespace that moved. The projections themselves did not change behaviour, so prose about the control loop stands.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "docs: describe the vertical-slice layout of the examples"
```

---

### Task 26: Write the vertical-slice skill

**Files:**
- Create: a skill directory via the `write-a-skill` workflow (it decides the path)

**Interfaces:**
- Produces: a reusable skill. No code depends on it.

- [ ] **Step 1: Invoke the skill-authoring skill**

```
/write-a-skill
```

- [ ] **Step 2: Supply the subject**

The skill teaches converting an event-sourced module from shared aggregate state to per-slice state. It should carry:

- **When to use it** — an event-sourced codebase where several actions fold one state record, especially one where narrowing interfaces (`IShipOrderState`) are already in place and give the appearance of slicing without the substance.
- **The core rule** — the event log is the only thing slices share. State, evolver, decision and transport belong to one action and live in one file.
- **The conversion recipe** — per slice: name the properties the decision actually reads; fold every event that can change any of them; keep the boundary as it was; move the transport method into the slice; delete the old interface and static method; run the schema check.
- **The error-message trap** — a guard may read one property while the refusal message reads another. `CanBeShipped` needs only `Status == Confirmed`, but `InvalidStatus("shipped", state.Status)` names the status, so the slice must fold every status-changing event or it silently changes a user-visible message. Generalised: **fold every event that can change anything the failure path reads, not just what the guard branches on.**
- **What stays global** — events and the vocabulary events are written in (status enums, problem codes, tag keys). Everything else is per slice.
- **The duplication rule** — N slices folding the same event N different ways is the pattern working, not a DRY violation. A shared `ApplyCreated` puts the shared object back.
- **Verification** — a schema snapshot test, taken before the first slice moves, is what makes each conversion a safe mechanical step.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "docs: add the vertical-slice-state skill"
```

---

## Deviations from the approved spec

Two, both deliberate, both flagged before execution:

1. **`ShipOrderEvolver` folds five events, not the spec's four.** The spec's sketch omits `OrderDelivered` on the grounds that a delivered order is not shippable either way. It is not either way: `OrderProblems.InvalidStatus("shipped", state.Status)` puts the status in the message and in `details["status"]`, so a slice that skipped `OrderDelivered` would tell a client that a delivered order "cannot be shipped in Shipped status". The same reasoning adds the fifth event to every status-guarded slice in both modules.

2. **Seven read slices become six.** The spec lists `GetOrders` and `GetRecentOrders` as separate slices; they are two queries over one EF projection, and a projection and every query over it change together. They share the `OrderSummaries` slice. `GetPayment`/`recentPayments` are unaffected — `GetPayment` folds the log and keeps its own slice.


