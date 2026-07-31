# Task 8 Report: Promote the public API surface to Shipped

## Per-project line counts after promotion

| Project | Shipped.txt lines | Unshipped.txt content |
|---------|------------------|-----------------------|
| Alberto | 1020 | `#nullable enable` only |
| Alberto.Commands | 60 | `#nullable enable` only |
| Alberto.EntityFramework | 26 | `#nullable enable` only |
| Alberto.InMemory | 44 | `#nullable enable` only |
| Alberto.Messaging | 108 | `#nullable enable` only |
| Alberto.Messaging.Postgres | 9 | `#nullable enable` only |
| Alberto.Postgres | 137 | `#nullable enable` only |
| Alberto.Telemetry | 10 | `#nullable enable` only |
| Alberto.Testing | 31 | `#nullable enable` only |
| Alberto.Testing.Xunit | 146 | `#nullable enable` only |

All `Shipped.txt` files have `#nullable enable` as line 1, followed by LC_ALL=C sorted, deduplicated declarations.
`Unshipped.txt` for each project was reset to a single `#nullable enable` line, which is what the analyzer accepts as "empty" (it allows the nullable header line without treating it as a pending API declaration).

## Build verification

### Build 1: Alberto.Tests
```
dotnet build tests/Alberto.Tests/Alberto.Tests.csproj -c Release
Result: Build succeeded, 81 Warning(s), 0 Error(s)
```
No RS00xx errors. The xUnit1051 warnings are pre-existing and unrelated to the API promotion.

### Build 2: Alberto.Examples.Tests
```
dotnet build tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj -c Release
Result: Build succeeded, 0 Error(s)
```

## Stale namespace check

```
git grep -n "Alberto\.Dcb\." -- 'src/*/PublicAPI.*.txt'
(no output — exit code 1, meaning no matches found)
```

All declarations use `Alberto.*` namespaces. No stale `Alberto.Dcb.` strings remain.

## Parked projects untouched

```
git diff --stat -- src/Alberto.Admin src/Alberto.Admin.Postgres src/Alberto.Commands.Analyzers
(no output — zero changes)
```

`src/Alberto.Admin/PublicAPI.Shipped.txt` still contains only `#nullable enable`.
`src/Alberto.Admin/PublicAPI.Unshipped.txt` still holds the full parked surface.
`Alberto.Admin.Postgres` and `Alberto.Commands.Analyzers` have no PublicAPI files at all (the analyzer is inactive for them as `IsPackable=false`).

## RS0017 demonstration

To prove the promotion has the intended effect, `ServiceCollectionExtensions` in
`src/Alberto.Telemetry/ServiceCollectionExtensions.cs` was temporarily changed from
`public static class` to `internal static class`.

### Build with class made internal (BEFORE restore):
```
src/Alberto.Telemetry/PublicAPI.Shipped.txt(2,1): error RS0017: Symbol
  'Alberto.Telemetry.ServiceCollectionExtensions' is part of the declared API,
  but is either not public or could not be found
src/Alberto.Telemetry/PublicAPI.Shipped.txt(6,1): error RS0017: Symbol
  'static Alberto.Telemetry.ServiceCollectionExtensions.AddAlbertoInstrumentation(
    this OpenTelemetry.Metrics.MeterProviderBuilder! builder) -> OpenTelemetry.Metrics.MeterProviderBuilder!'
  is part of the declared API, but is either not public or could not be found
src/Alberto.Telemetry/PublicAPI.Shipped.txt(7,1): error RS0017: Symbol
  'static Alberto.Telemetry.ServiceCollectionExtensions.AddAlbertoInstrumentation(
    this OpenTelemetry.Trace.TracerProviderBuilder! builder) -> OpenTelemetry.Trace.TracerProviderBuilder!'
  is part of the declared API, but is either not public or could not be found
Build FAILED. 3 Error(s)
```

### Build after restoring class to public (AFTER restore):
```
Build succeeded.
```

The analyzer now enforces the frozen surface: removing or hiding a public member without
editing `PublicAPI.Shipped.txt` is a hard build error. This is the point — it is the
moment you learn you are breaking consumers.

## Commit

```
SHA: 53af833
Message: chore: promote the public API surface to Shipped ahead of 0.1.0
20 files changed, 1591 insertions(+), 1581 deletions(-)
```
