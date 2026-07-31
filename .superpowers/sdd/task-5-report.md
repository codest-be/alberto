# Task 5 Report: Strip `Alberto.Dcb` to `Alberto`

## What Was Done

### Before-count
4,024 occurrences of `Alberto.Dcb` across tracked files (excluding `docs/superpowers/` and `CHANGELOG.md`).

### Step 1: Text substitution
Ran `perl -pi -e 's/Alberto\.Dcb/Alberto/g'` via `git ls-files -z | xargs -0` (null-terminated to handle filenames with spaces). An earlier attempt with plain `xargs` (without `-0`) partially ran before failing on migration files with spaces in their names (`20260118122739_version column.cs`). This caused a **double-substitution problem** on files without spaces: `Alberto.Dcb.DcbQuery` → (first pass) `Alberto.DcbQuery` → (second pass) `AlbertoQuery`.

### Step 2: Fix double-substitution errors
The double-substitution affected 9 `PublicAPI.Unshipped.txt` files and one test file (`CrossTenantProjectionContractTests.cs`). The affected type names were:
- `AlbertoQuery` → corrected to `Alberto.DcbQuery`
- `AlbertoModuleBuilder` → corrected to `Alberto.DcbModuleBuilder`
- `AlbertoModuleBuilderExtensions` → corrected to `Alberto.DcbModuleBuilderExtensions`
- `AlbertoConflictException` → corrected to `Alberto.DcbConflictException`

Applied via in-order `perl -pi -e` substitutions (longest string first to avoid partial matches).

### Step 3: Directory renames (git mv)
18 directories renamed, 18 project files renamed, covering `src/`, `tests/`, `benchmarks/` directories. The two bridge projects already had the correct feature-first ordering (`Alberto.Dcb.Messaging.Postgres`, `Alberto.Dcb.Admin.Postgres`) from Task 4, confirmed on disk before moving.

### Step 4: Manual fixes required beyond the mechanical pass

**Fix 1: Broken relative namespace reference**
`tests/Alberto.Tests/Tenancy/ShardedPostgresFixture.cs` line 59 had a relative namespace reference `Dcb.Tenancy.ITenantShardMap`. In the original, this was a C#-level relative reference from namespace `Alberto.Dcb.Tests.Tenancy`. After the rename the file moved to `Alberto.Tests.Tenancy` and the reference broke. Fixed by changing to fully qualified `Alberto.Tenancy.ITenantShardMap`.

**Fix 2: CS1614 TagAttribute ambiguity in contracts files**
Two files (`Alberto.Orders/Contracts/OrderEvents.cs` and `Alberto.Payments/Contracts/PaymentEvents.cs`) got CS1614 errors after the rename. Root cause: the files are in sub-namespaces of `Alberto` (`Alberto.Orders.Contracts`, `Alberto.Payments.Contracts`). Before the rename, `using Alberto.Dcb;` imported `Alberto.Dcb.TagAttribute` from a sibling namespace — no conflict. After the rename, `using Alberto;` imports `Alberto.TagAttribute` from the *parent* namespace, which C# also makes accessible via implicit parent-namespace scoping. This creates two paths to the same type, and the `using Tag = Alberto.TagAttribute;` alias triggers CS1614 when combined with the HotChocolate.Types global using (which provides a competing `TagAttribute`).

Fix applied: removed the redundant `using Alberto;` directive (already available via parent namespace scope) and changed `[property: Tag(…)]` to `[property: @Tag(…)]` in both files. The `@` prefix is the compiler-recommended resolution (stated in the error message), bypassing attribute-shorthand expansion.

## Verification Results

### 1. `Alberto.Dcb` text check
```
git grep -n "Alberto\.Dcb" -- ':(exclude)docs/superpowers/' ':(exclude)CHANGELOG.md'
```
Returns matches ONLY for `Alberto.DcbQuery`, `Alberto.DcbModuleBuilder`, `Alberto.DcbModuleBuilderExtensions`, `Alberto.DcbConflictException` in `PublicAPI.Unshipped.txt` files — these are type names (class/interface identifiers) in the `Alberto` namespace, not old namespace references. All are correct and intended to survive.

```
git grep -n "Alberto\.Dcb\." -- ':(exclude)docs/superpowers/' ':(exclude)CHANGELOG.md'
```
Returns nothing (exit=1). No `Alberto.Dcb.` (namespace separator with trailing dot) remains.

### 2. DcbQuery/DcbModuleBuilder survive
```
git grep -n "DcbQuery\|DcbModuleBuilder" -- src/ | wc -l
```
→ 310 hits. Type names in source code are intact.

### 3. Embedded resource prefix
```
grep -n 'prefix = ' src/Alberto.Postgres/PostgresMigrator.cs
```
→ Line 308: `var prefix = $"Alberto.Postgres.{folderPath}.";`

### 4. AlbertoMetrics Name constant
```
grep -n 'const string Name' src/Alberto/Telemetry/AlbertoMetrics.cs
```
→ Line 16: `public const string Name = "Alberto";`

### 5. Build: Alberto.Tests
```
dotnet build tests/Alberto.Tests/Alberto.Tests.csproj -c Release
```
→ **Build succeeded.**

### 6. Build: Alberto.Examples.Tests
```
dotnet build tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj -c Release
```
→ **Build succeeded.**

### 7. Build: Alberto.Cli
```
dotnet build tools/Alberto.Cli/Alberto.Cli.csproj -c Release
```
→ **Build succeeded.**

### 8. Build: Alberto.Admin and Alberto.Admin.Postgres
```
dotnet build src/Alberto.Admin/Alberto.Admin.csproj -c Release
dotnet build src/Alberto.Admin.Postgres/Alberto.Admin.Postgres.csproj -c Release
```
→ **Build succeeded.** (both)

### 9. Full test suite (Alberto.Tests)
```
dotnet test tests/Alberto.Tests/Alberto.Tests.csproj -c Release --no-build --logger "console;verbosity=quiet"
```
→ **Failed: 0, Passed: 1605, Skipped: 16, Total: 1621** — Docker was running; Testcontainers tests including migration parity tests passed.

### 10. Examples tests
```
dotnet test tests/Alberto.Examples.Tests/Alberto.Examples.Tests.csproj -c Release --no-build
```
→ **Failed: 0, Passed: 76, Skipped: 0, Total: 76** — includes GraphQL schema snapshot test.

### 11. Benchmark smoke run
```
dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --job dry --anyCategories=smoke
```
→ Completed without error. Run time: ~2 minutes, executed benchmarks: 8.

### 12. Workflow allowlist vs Pack loop diff
Pack loop projects (from `publish-packages.yml`), sorted LC_ALL=C:
```
Alberto
Alberto.Commands
Alberto.EntityFramework
Alberto.InMemory
Alberto.Messaging
Alberto.Messaging.Postgres
Alberto.Postgres
Alberto.Telemetry
Alberto.Testing
Alberto.Testing.Xunit
```
Allowlist (from the `cat > /tmp/expected-packages.txt` heredoc), sorted LC_ALL=C:
```
Alberto
Alberto.Commands
Alberto.EntityFramework
Alberto.InMemory
Alberto.Messaging
Alberto.Messaging.Postgres
Alberto.Postgres
Alberto.Telemetry
Alberto.Testing
Alberto.Testing.Xunit
```
→ **MATCH — no diff.** The allowlist is correctly `LC_ALL=C`-sorted.

### Pack smoke — 10 expected .nupkg files
```
Alberto.0.1.0-ci.nupkg
Alberto.Commands.0.1.0-ci.nupkg
Alberto.EntityFramework.0.1.0-ci.nupkg
Alberto.InMemory.0.1.0-ci.nupkg
Alberto.Messaging.0.1.0-ci.nupkg
Alberto.Messaging.Postgres.0.1.0-ci.nupkg
Alberto.Postgres.0.1.0-ci.nupkg
Alberto.Telemetry.0.1.0-ci.nupkg
Alberto.Testing.0.1.0-ci.nupkg
Alberto.Testing.Xunit.0.1.0-ci.nupkg
```
Exactly 10, exactly matching the expected set.

## Commit
SHA: `f61335a`
Message: `refactor!: rename Alberto.Dcb.* to Alberto.* across packages, assemblies and namespaces`
Files changed: 591, insertions: 3254, deletions: 3255.

## Notes for Next Tasks
1. The `Alberto.Tenancy.ITenantShardMap` reference fix (`ShardedPostgresFixture.cs`) was an expected consequence of the namespace rename — a C# relative namespace reference that worked under `Alberto.Dcb.Tests.Tenancy` broke under `Alberto.Tests.Tenancy`.
2. The `@Tag` syntax change in the Contracts files is the right C# fix for CS1614. The `using Tag = Alberto.TagAttribute;` alias remains; only the attribute usage site changed to `@Tag` to bypass shorthand expansion. This is semantically identical at runtime.
3. The OpenTelemetry meter/ActivitySource name `"Alberto"` (was `"Alberto.Dcb"`) is an observable change for dashboards. Left as-is per brief. Record in changelog in a later task.
4. The embedded-resource prefix change in `PostgresMigrator.cs` means existing databases will see all previously-applied scripts as "pending" on next deploy. Left as-is per brief. Record in changelog in a later task.
