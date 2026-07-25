# DcbQuery and command pipeline DX — design

**Date:** 2026-07-25
**Scope:** `src/Alberto.Dcb`, `src/Alberto.Dcb.Commands`, `src/Alberto.Dcb.Postgres`, `src/Alberto.Dcb.InMemory`, `apps/Alberto.Orders`, `apps/Alberto.Payments`
**Status:** approved, not yet implemented
**Breaking changes:** permitted, no deprecation cycle required

## Problem

Alberto ships two command-handling APIs and a query model, and its own example
application uses none of them correctly.

**The example app has no DCB conflict check.** `OrderMutations` and
`PaymentMutations` load state, decide, and then call `AppendAsync` without an
`expectedPosition`:

```csharp
// apps/Alberto.Orders/Alberto.Orders.Api/GraphQL/Mutations/OrderMutations.cs:207
await eventStore.AppendAsync(toPersist, OrderBoundary.BoundaryFor(orderId), cancellationToken: ct);
```

The boundary query is passed; the observed position is not. Every mutation in
the flagship example is a read-then-blind-append. The `CommandPipeline` exists
precisely to make that impossible, and nothing outside `tests/` uses it.

**There are three parallel result types.** `DecisionResult<TEvent>` is marked
`[Obsolete]` yet is the only decision type the domain layer uses — all 8
`Orders.Core/Order/Actions/*` and all 5 `Payments.Core/Payment/Actions/*` return
it, producing **76 CS0618 warnings** on a full solution build with nothing
suppressing them. The deprecation stalled for two structural reasons:

1. `Decision` lives in `Alberto.Dcb.Commands`; both `Alberto.Orders.Core` and
   `Alberto.Payments.Core` reference only `Alberto.Dcb`. The domain layer cannot
   see its own replacement type.
2. `DecisionResult.Failure(string)` maps to `Decision.Fail(Problem)`, and
   `Problem.Code` is `required`. Migrating means inventing an error code for
   roughly fifty failure sites — a modelling decision nobody made, so everyone
   kept the string.

**The query model has metastasized into the schema.** `DcbQuery` carries two
mode flags (`TagMatchMode`, `CompositionMode`) and exposes seven booleans for
backends to switch on. Downstream that costs:

- **11 distinct `alberto_read_by_*` SQL functions**, one per shape in the cross
  product, defined 22× per migration set, across **two** sets (multi-tenant and
  `SingleTenant/`).
- **6 versions of `alberto_append_events`**, selected by a 3-flag matrix in
  `PostgresEventStoreBackend.cs:105`. The code comment enumerates them: *"v4:
  intersect with exact tags. v5: intersect with wildcards. v6: intersect with
  all-tags. v3: union with all-tags…"*

Every new query shape costs a new SQL function version in two migration sets.

The model is also *less expressive* than DCB requires. Two axes combined by
global flags can express `(A|B) AND (t1|t2)`, but not the canonical DCB shape:

```
(OrderPlaced tagged order:1) OR (CustomerBanned tagged customer:9)
```

`AsUnion` approximates it by over-matching, and is documented as "legacy" in a
v3 codebase.

## Goals

1. The guided path is shorter than the hand-rolled one, measured by porting
   `OrderMutations` and `PaymentMutations`.
2. Losing the conflict check requires an explicitly-named call, never an omitted
   argument.
3. One result type reaching the domain layer, zero CS0618 warnings.
4. A query model that expresses per-item type/tag pairing, and that does not
   require a new SQL function per shape.
5. No read or append throughput regression.

## Non-goals

- Deprecation shims. The API breaks.
- Reworking projections, reactors, the outbox, or rebuilds.
- The known gaps in `CLAUDE.md` (promotion window, orphaned outbox entries,
  JSONB read-side tenant mismatch) are out of scope.

---

## 1. Result-type consolidation

**Move `Decision` and `Decision<T>` from `Alberto.Dcb.Commands` into
`Alberto.Dcb`**, alongside `Result` and `Problem`. A decision is a domain
concept, not a pipeline concept; this removes blocker (1) without adding a
project reference from the domain layer to the command layer.

**Delete `src/Alberto.Dcb/DecisionResult.cs`.** `EnsureSuccess()` does not come
along — mutations return `Result`, and the GraphQL layer maps `Problem.Code`
rather than catching `InvalidOperationException`.

**Add a two-string failure overload** so the migration does not require fifty
`Problem.Create` calls written by hand:

```csharp
Decision.Fail("order.not-found", $"Order {id} does not exist");
Decision.Fail(OrderProblems.InvalidStatus(state.Status));   // richer, with Details
```

No implicit `string → Problem` conversion — that reintroduces codeless failures.

**Error code convention:** `{aggregate}.{kebab-reason}`. Codes live in per-domain
static classes (`OrderProblems`, `PaymentProblems`) so the taxonomy is reviewable
in one file rather than scattered across 13 action files.

```csharp
public static class OrderProblems
{
    public static Problem NotFound(Guid id) =>
        Problem.Create("order.not-found", $"Order {id} does not exist");

    public static Problem InvalidStatus(OrderStatus status) =>
        Problem.Create("order.invalid-status", $"Order cannot be modified in {status} status",
            new Dictionary<string, object> { ["status"] = status });
}
```

## 2. Pipeline reshape

### Stages

```csharp
store.Handle(cmd)                       // CommandPipeline<TCommand>
     .Validate(rules)                   // optional, chainable → CommandPipeline<TCommand>
     .Enrich(async (c, ct) => …)        // optional, async → CommandPipeline<TCommand>
     .Load(boundary, evolver)           // → BoundPipeline<TCommand, TState>
     .Decide(Actions.Confirm)           // synchronous, pure → BoundDecision<TValue>
     .RetryOnConflict(3)                // optional
     .Commit(ct);                       // Task<Result<TValue>>
```

`NoValidation()` is deleted. `Validate` returns the same type, so it may be
called zero, one, or many times. `ValidatedPipeline<T>` disappears as a type.

### Bound versus unbound

| Call | Returns | Terminal operations |
|---|---|---|
| `Load(boundary, initial, apply)` | `BoundPipeline` | `Commit(ct)`, `TryCommit(ct)` |
| `Load(boundary, evolver)` | `BoundPipeline` | ″ |
| `Load<TState>(boundary)` — evolver from DI | `BoundPipeline` | ″ |
| `LoadUnbound(fn)` | `UnboundPipeline` | `Commit(query, position, ct)`, `CommitUnconditionally(ct)` |
| `.Decide(…)` directly off `Handle` | `UnboundDecision` | ″ |

`Commit(ct)` exists only where a boundary exists. The runtime throw at
`Functional.cs:233` and its three-sentence remediation message are deleted; the
compiler states the constraint instead.

`Load(boundary, Evolver<TState>)` is what makes the app port small — the example
apps already use `Evolver<T>` with `IEvolve<TEvent>` and a cached dispatcher,
while the pipeline today only accepts a raw `Func<TState, IEvent, TState>`. The
two subsystems stop being parallel worlds.

### Commit and conflicts

- `Commit(ct)` throws `DcbConflictException` (today's behavior).
- `TryCommit(ct)` returns `Result` carrying a `dcb.conflict` problem.
- `RetryOnConflict(n)` sits between `Decide` and `Commit`. Feasible without
  restructuring: `_load` and the decide function are already deferred closures.

### Enrich

`Decide` becomes **synchronous only**; the async overloads are deleted. IO moves
to `Enrich`, an async `Map` over the command that runs *before* `Load`.

Three reasons, escalating:

1. Deciders become unit-testable without a harness.
2. **Async `Decide` widens the conflict window.** The boundary position is
   captured at `Load`; anything awaited inside `Decide` happens after the
   observation and before the append. Correctness holds — the position check
   still fires — but slow caller IO turns rare conflicts into routine ones and
   the store gets blamed. `CommandPipelineTests.cs:59` exploits exactly this to
   *force* a conflict.
3. **Async `Decide` is incompatible with `RetryOnConflict`.** Retry must re-run
   load and decide. A decision that charged a card charges twice, and the call
   site cannot show it. A separate stage makes the contract expressible:
   **retry re-runs `Load` and `Decide`, never `Enrich`.**

Enrichment is an async command transform, so it needs no new type parameter —
`CommandPipeline<T>` already has `Map`. `Decide` stays 2-arg.

Consequence: `CommandPipelineTests.cs:59` and its sibling stop compiling. They
become `Enrich` tests, plus a dedicated conflict test that appends between
`Load` and `Commit` rather than inside `Decide`.

If enrichment genuinely depends on loaded state, that is `LoadUnbound` plus an
explicit `Commit(query, position, ct)` — off the guided path, caller owns the
window.

### Incidental fixes

- `Map`'s `default!` command disappears with `ValidatedPipeline`; failures
  short-circuit by pipeline state, not by a null payload.
- Pipeline types stay `readonly struct`. `default(T)` currently yields a null
  `_store` and an NRE on first use; each terminal operation guards `_store` and
  throws a named error instead.

## 3. Query model

`DcbQuery` becomes an ordered list of **query items**, OR'd across. Each item is
`{types, tags}`: **any** of the types AND **all** of the tags.

```csharp
DcbQuery.For(Tags.Order, id)                              // 1 item, 1 tag
DcbQuery.For(Tags.Student, sid).AndTag(Tags.Course, cid)  // 1 item, 2 tags — AND visible
DcbQuery.For(Tags.Order, id).WithType<OrderPlaced>()
        .Or(DcbQuery.ByTypes<GlobalLockAcquired>())       // 2 items — OR visible
```

**Removed from the public surface:** `TagMatchMode`, `CompositionMode`,
`ByAllTags`, `AsUnion`, `AsIntersect`, and all seven shape booleans
(`HasTypesOnly`, `HasTagsOnly`, `HasTypesAndTags`, `IntersectsTypesAndTags`,
`UnionsTypesAndTags`, `RequiresAllTags`, `HasWildcardPatterns`).

**Retained as single-item sugar:** `ByTypes`, `ByTagPatterns`, `For`, `Empty`,
`WithType<T>`, `WithTypes`, `WithTagPatterns`, `WithTagPrefix`.

**Renamed:** `WithTag` / `WithTags` become `AndTag` / `AndTags`. The name now
states the semantics — tags within an item are AND'd — where `With` was silent
about it. This is the rename that makes `ByAllTags`'s deletion safe.

**Collapsed:** the five `For` overloads become `For(TagConcept, string)` plus one
generic `For<TId>`.

**Added:** `ByTypes<TEvent>()` and `Or(DcbQuery)`. The former already exists in
`Type[]` form only; the generic form removes `typeof` noise from call sites.

**Added:** `TagConcept`, a value type replacing bare concept strings.
`Tags.Order.Of(id)` and `Tags.Order.Any()` remove `"order:*"` string parsing from
call sites and make a typo in either half a compile error.

Three gains beyond readability:

1. **Expressiveness.** `(OrderPlaced tagged order:1) OR (CustomerBanned tagged
   customer:9)` is inexpressible today; it is two items here.
2. **An illegal state becomes legal.** `TagMatchMode.All` with a wildcard throws
   at construction (`DcbQuery.cs:167`). Per-item it is well-defined and useful:
   "carries some `order:*` tag AND exactly `customer:9`."
3. **`ByTags(a, b)` stops silently meaning OR.** It has no successor spelling;
   the `.Or(…)` is written by hand and visible in review.

`ToString()` renders items as `(types=[…] AND tags=[…]) OR (…)`.

### SQL strategy — spike with a benchmark gate

The target is one general compilation replacing 11 read functions and 6 append
versions per tenant set. The risk is that migrations `004_OptimizeReadByTags`,
`005_LimitReadByTagsBeforeEventJoin` and `009_OptimizeQueryFunctions` exist
because someone measured, and a generic JSONB-driven predicate may not preserve
those plans.

**Spike, in order:**

1. **UNION-of-per-item-selects, generated in C#.** Each branch has the shape the
   tuned functions already use, so each branch stays index-friendly and the
   general case is N planner-independent branches. One code path *and* tuned
   plans. Cost: SQL construction moves client-side, whereas this codebase
   currently keeps everything in stored functions — a genuine architectural
   shift, to be decided on the spike's evidence.
2. **Fallback:** preserve specialized functions for single-item `tags-only` and
   `types-only` — the only shapes either example app uses — and route the other
   nine shapes to one general function.

**Gate:** `benchmarks/Alberto.Dcb.Benchmarks` read and append results, before and
after. No regression beyond noise on the `For(concept, id)` path, which dominates
real usage. The decision between (1) and (2) is made on those numbers, not in
review.

Both InMemory backends (`InMemoryEventStoreBackend.cs:55`, `:92`, `:281`) compile
the same item list; they have no plan concerns and follow the general path.

## 4. Port the example apps

`OrderMutations` and `PaymentMutations` move onto the pipeline. This is the
acceptance test for sections 1–3 and the fix for the missing `expectedPosition`
— the conflict check returns because `Commit(ct)` is the only terminal available
after a bounded `Load`.

**Exit criterion:** if a ported mutation is not visibly shorter than the
hand-rolled version, sections 1–3 did not land and are revised before the port
continues.

## 5. Testing

`EventStoreBackendSpecification` is the leverage point — it already runs against
both backends. New semantics land there so InMemory, multi-tenant Postgres and
single-tenant Postgres are held to one definition:

- multi-item OR
- wildcard plus exact tag within a single item (today: a construction throw)
- per-item type/tag pairing that the flag model could not express
- append-side boundary checking for each of the above

`DcbQueryTests` narrows to construction, normalization and `ToString`.

`CommandPipelineTests` gains type-state coverage: an unbound pipeline must not
expose `Commit(ct)`, verified as a compile-fail test or an analyzer assertion.

## Sequencing

Ordered by dependency, not by value — all four are in scope.

| # | Step | Unblocks | Risk |
|---|---|---|---|
| 1 | `Problem` taxonomy, `Decision` moves to core, `DecisionResult` deleted | everything | low — mechanical once codes exist |
| 2 | Pipeline reshape (stages, bound/unbound split, `Enrich`, `Commit`) | step 3 | low — additive and type-state, no backend change |
| 3 | Port `OrderMutations` + `PaymentMutations` | validates 1–2 | low — reveals whether the DX landed |
| 4 | Query item model + SQL spike | — | **high** — touches both migration sets and every backend |

Step 4 is last so the app port is not written twice: its call-site surface
(`For`, `ByTypes`) barely changes, but its backend surface changes completely.

## Open decisions

| Decision | Resolved by |
|---|---|
| Generated SQL versus stored functions for query compilation | the step-4 spike, on benchmark evidence |
| Whether any specialized read functions survive, and which | same spike |

## Success criteria

1. `dotnet build AlbertoV3.slnx` emits zero CS0618 warnings (from 76).
2. `DecisionResult.cs` is deleted; one decision type reaches the domain layer.
3. Every `OrderMutations` and `PaymentMutations` write path commits under an
   observed position; no `AppendAsync` call omits `expectedPosition`.
4. A ported mutation is shorter than the hand-rolled version it replaces.
5. `Commit(ct)` is unreachable without a boundary — enforced by the compiler, not
   by an exception.
6. `TagMatchMode` and `CompositionMode` no longer exist; backends switch on an
   item list, not on seven booleans.
7. Read and append benchmarks show no regression on the `For(concept, id)` path.
