# DX-6 — .WithTenancy() after .WithPostgres() now fails loudly at startup

## What changed

A `TenancyOrderingValidator` hosted service is registered by `WithPostgres()`. At application
startup, it checks whether `DcbModuleBuilder.HasTenancy` changed between the time
`WithPostgres()` was called and the time the application starts. If it did, the call ordering
was wrong and startup throws `InvalidOperationException`.

## Why

Previously, calling `.WithPostgres()` before `.WithTenancy()` silently registered a
single-tenant backend and ignored the tenancy flag entirely — no error, no warning, just
single-tenant behaviour while the application believes it is multi-tenant. This is a
configuration trap that is trivially hit when code is reorganised.

## Impact

**Breaking for configurations that call `.WithTenancy()` after `.WithPostgres()`.**

If you see this error at startup:
```
Alberto configuration error for module 'orders': .WithTenancy() was called AFTER .WithPostgres(),
so the backend was wired in single-tenant mode and will silently ignore the tenancy flag.
```

## Migration

Reorder the fluent chain so `.WithTenancy()` comes before `.WithPostgres()`:

```csharp
// BEFORE (wrong order — silently single-tenant)
builder.Services.AddAlberto("orders", module =>
    module
        .WithPostgres(o => o.ConnectionString = "...")
        .WithTenancy());

// AFTER (correct order)
builder.Services.AddAlberto("orders", module =>
    module
        .WithTenancy()
        .WithPostgres(o => o.ConnectionString = "..."));
```
