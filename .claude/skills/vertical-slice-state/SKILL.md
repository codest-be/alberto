---
name: vertical-slice-state
description: Convert an event-sourced module from one shared aggregate state record to per-slice state — each action owning its state, evolver, decision and transport in one file, sharing only the event log. Use when several commands fold the same state record, when narrowing interfaces like IShipOrderState give the appearance of slicing without the substance, or when the user asks for vertical slice architecture, VSA, state per slice, or per-feature state in an event-sourced or CQRS codebase.
---

# Per-slice state in an event-sourced module

## The core rule

**The event log is the only thing slices share.** State, evolver, decision function, consistency
boundary and transport (GraphQL field, endpoint, handler) belong to one action and live in one file.

Narrowing interfaces are not slicing. `ShipOrderState : IShipOrderState` still forces every action
onto one record: a property added for one behaviour is visible to all of them, and the interface
only hides it at the point of use. Delete the record, not the coupling.

## When it applies

- Several commands `Load` the same state type, or one `partial` decider carries every action's
  `Apply` methods.
- One state record has a property that means different things to two actions (the classic pair: an
  *initiated* amount and a *captured* amount, both called "the amount").
- The user asks for vertical slices, VSA, "state per slice", or feature folders.

Do **not** apply it to read models. A projection and every query over it change together — group
read slices by read model, not by field.

## Workflow

Before the first slice moves, establish the safety net: **snapshot the external contract**
(GraphQL SDL, OpenAPI, handler signatures) in a test that fails on any diff. Every conversion is
then a mechanical step you can verify. Without it you are refactoring blind.

Then, one slice at a time — one commit each:

1. **Name the properties the decision actually reads.** Usually two or three, not twelve.
2. **Fold every event that can change anything the failure path reads** — not just what the guard
   branches on. See the trap below.
3. **Keep the boundary byte-identical.** Narrowing it is a separate decision with its own
   justification; do not smuggle it into a mechanical move.
4. **Move the transport method into the slice file**, unchanged.
5. **Delete** the old narrowing interface and the old static decision method.
6. **Run the contract snapshot and the tests.** Commit.

Finish by deleting the shared record, its evolver and its decider — that deletion is what makes the
slicing true. Then update the docs that name the moved types.

Detailed recipe, worked example and verification commands: [REFERENCE.md](REFERENCE.md)

## The error-message trap

A guard reads one property; the *refusal message* often reads another.

```csharp
public bool CanBeShipped => Status == OrderStatus.Confirmed;   // guard: one value
return Decision.Fail(OrderProblems.InvalidStatus("shipped", state.Status));  // message: all of them
```

`CanBeShipped` only needs to know whether the status is `Confirmed`, so it is tempting to fold only
`OrderConfirmed` and `OrderShipped`. But the refusal names the status, so a slice that skipped
`OrderDelivered` would tell a client that a delivered order "cannot be shipped in Shipped status" —
a silent, user-visible regression that compiles and passes any test that only checks the failure
*happened*.

**Generalised: fold every event that can change anything the failure path reads, not just what the
guard branches on.** Pin it with a test asserting the refusal message names the real status.

## What stays global

Events, and the vocabulary events are written in: status enums (persisted by name, exposed in the
schema), problem/error codes (a client contract), tag or stream keys (boundaries are built from
them, so two spellings silently stop conflicting). Put them in a folder named for what it is —
`Contracts/` — so they cannot be mistaken for domain code that happens to be shared.

Everything else is per slice.

## The duplication rule

N slices folding the same event N different ways is the pattern working, not a DRY violation. Each
one projects a different part of it.

**If two slices want the same helper, they get two copies.** A shared `ApplyCreated` or a base state
record puts the shared object back under a new name. The only legitimate exceptions are the contract
vocabulary above and pure policy constants (a retry count) that carry no domain meaning.
