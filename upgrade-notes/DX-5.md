# DX-5 — PostgresOutboxStore moved to Alberto.Dcb.Postgres.Messaging

## What changed

`PostgresOutboxStore` has been extracted from the `Alberto.Dcb.Postgres` package into a new
dedicated package: **`Alberto.Dcb.Postgres.Messaging`**.

Before this change, adding a NuGet reference to `Alberto.Dcb.Postgres` silently pulled in
`Alberto.Dcb.Messaging` as a transitive dependency, forcing every Postgres user to take a
dependency on the outbox/messaging stack they might not need.  The new package exists at the
intersection of both concerns; consumers that do not use the outbox no longer acquire the
messaging dependency.

### Type moves

| Before | After |
|--------|-------|
| `Alberto.Dcb.Postgres.PostgresOutboxStore` (in package `Alberto.Dcb.Postgres`) | `Alberto.Dcb.Postgres.Messaging.PostgresOutboxStore` (in package `Alberto.Dcb.Postgres.Messaging`) |

## Why

`Alberto.Dcb.Postgres` should be a standalone persistence backend with no opinion about
messaging.  Callers who only want Postgres event storage should not be forced to reference the
outbox interfaces and relay infrastructure from `Alberto.Dcb.Messaging`.

## Migration guide for integrators

### 1. Add the new package reference

```xml
<!-- Before: nothing extra needed — PostgresOutboxStore came in transitively -->

<!-- After: add the explicit package reference -->
<PackageReference Include="Alberto.Dcb.Postgres.Messaging" Version="x.x.x" />
```

### 2. Update the `using` directive

```csharp
// Before
using Alberto.Dcb.Postgres;

// After
using Alberto.Dcb.Postgres.Messaging;
```

The type name `PostgresOutboxStore` and its constructor signature are unchanged:

```csharp
// No change needed on the instantiation side
var outboxStore = new PostgresOutboxStore(dataSource, schema: "orders");
```

### 3. For projects that do NOT use PostgresOutboxStore

No action required.  The `Alberto.Dcb.Postgres` package no longer references
`Alberto.Dcb.Messaging`, so messaging types will disappear from your transitive closure if you
do not add `Alberto.Dcb.Messaging` or `Alberto.Dcb.Postgres.Messaging` explicitly.  If you
were depending on that transitive reference for other messaging types, add
`Alberto.Dcb.Messaging` directly.
