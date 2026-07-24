# DX-8 — ReactTo arity-ladder overloads removed

## What changed

Six `ReactTo` overloads that accepted statically-typed dependency parameters (`TDep`,
`TDep1`/`TDep2`, `TDep1`/`TDep2`/`TDep3`) in both context-less and context-aware variants
have been **deleted** from `DcbModuleBuilderExtensions`.

The two supported shapes are now:

| Shape | Signature |
|---|---|
| **Factory form** | `ReactTo<TEvent>(Func<IServiceProvider, Func<TEvent, CancellationToken, Task>>, ...)` |
| **Factory form with context** | `ReactTo<TEvent>(Func<IServiceProvider, Func<TEvent, ReactorContext, CancellationToken, Task>>, ...)` |
| **Handler-class form** | `ReactTo<TEvent, THandler>(Func<THandler, Func<TEvent, CancellationToken, Task>>, ...)` |
| **Handler-class form with context** | `ReactTo<TEvent, THandler>(Func<THandler, Func<TEvent, ReactorContext, CancellationToken, Task>>, ...)` |

## Why

The arity ladder (×2 context variants × 3 dependency arities = 6 overloads) added cognitive
overhead without adding power — every call site was a thin wrapper around the factory form.
As dependency count grew, callers had to know which arity overload to pick. The factory form
already handles any number of dependencies with full IntelliSense support via
`sp.GetRequiredService<T>()`.

## Impact

**Breaking for any caller of the removed overloads:**
- `ReactTo<TEvent, TDep>(Func<TDep, TEvent, CancellationToken, Task>, ...)`
- `ReactTo<TEvent, TDep>(Func<TDep, TEvent, ReactorContext, CancellationToken, Task>, ...)`
- `ReactTo<TEvent, TDep1, TDep2>(Func<TDep1, TDep2, TEvent, CancellationToken, Task>, ...)`
- `ReactTo<TEvent, TDep1, TDep2>(Func<TDep1, TDep2, TEvent, ReactorContext, CancellationToken, Task>, ...)`
- `ReactTo<TEvent, TDep1, TDep2, TDep3>(Func<TDep1, TDep2, TDep3, TEvent, CancellationToken, Task>, ...)`
- `ReactTo<TEvent, TDep1, TDep2, TDep3>(Func<TDep1, TDep2, TDep3, TEvent, ReactorContext, CancellationToken, Task>, ...)`

The two-type-parameter form `ReactTo<TEvent, THandler>` that takes a **method selector**
(`Func<THandler, Func<TEvent, ...>>`) is **not affected** — it targets the handler-class form,
which is kept as a primary supported shape.

## Migration

Replace each arity-ladder call with the factory form. Resolve dependencies from the
`IServiceProvider` argument; they are captured once at startup, identical to the old behaviour.

### Single dependency, no context

```csharp
// BEFORE
builder.ReactTo<OrderPlaced, EmailService>(
    (svc, e, ct) => svc.SendConfirmationAsync(e.OrderId, ct),
    "order-email-reactor");

// AFTER
builder.ReactTo<OrderPlaced>(
    sp =>
    {
        var svc = sp.GetRequiredService<EmailService>();
        return (e, ct) => svc.SendConfirmationAsync(e.OrderId, ct);
    },
    "order-email-reactor");
```

### Single dependency, with ReactorContext

```csharp
// BEFORE
builder.ReactTo<OrderPlaced, AuditLog>(
    (log, e, context, ct) => log.RecordAsync(e.OrderId, context.Timestamp, ct),
    "order-audit-reactor");

// AFTER
builder.ReactTo<OrderPlaced>(
    sp =>
    {
        var log = sp.GetRequiredService<AuditLog>();
        return (e, context, ct) => log.RecordAsync(e.OrderId, context.Timestamp, ct);
    },
    "order-audit-reactor");
```

### Two dependencies

```csharp
// BEFORE
builder.ReactTo<OrderPlaced, EmailService, AuditLog>(
    (svc, log, e, ct) => ...,
    "order-reactor");

// AFTER
builder.ReactTo<OrderPlaced>(
    sp =>
    {
        var svc = sp.GetRequiredService<EmailService>();
        var log = sp.GetRequiredService<AuditLog>();
        return (e, ct) => ...;
    },
    "order-reactor");
```

The pattern scales to three or more dependencies without needing a new overload.

### Handler-class form is unchanged

Calls using the method-selector form are unaffected:

```csharp
// Unchanged — this is the handler-class form, not the arity ladder
builder.ReactTo<OrderPlaced, OrderReactor>(h => h.HandleAsync, "order-reactor");
```
