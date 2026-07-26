# Task 8 Report: Ship Both Packages

## Files touched

- `.github/workflows/publish-packages.yml` — two edits:
  1. Added two `dotnet build` lines after `Alberto.Dcb.Telemetry` in the `Build libraries` step.
  2. Extended the `for proj in …` list in the `Pack` step to include `Alberto.Dcb.Testing Alberto.Dcb.Testing.Xunit`.

## Workflow diff (both edits)

**Build libraries step** — added two lines after `Alberto.Dcb.Telemetry`:
```yaml
          dotnet build src/Alberto.Dcb.Testing/Alberto.Dcb.Testing.csproj -c Release
          dotnet build src/Alberto.Dcb.Testing.Xunit/Alberto.Dcb.Testing.Xunit.csproj -c Release
```

**Pack step** — new `for` list:
```yaml
          for proj in Alberto.Dcb Alberto.Dcb.Commands Alberto.Dcb.EntityFramework Alberto.Dcb.InMemory Alberto.Dcb.Messaging Alberto.Dcb.Postgres Alberto.Dcb.Postgres.Messaging Alberto.Dcb.Telemetry Alberto.Dcb.Testing Alberto.Dcb.Testing.Xunit; do
```

**Push step confirmed** — unchanged at:
```yaml
run: dotnet nuget push "artifacts/Alberto.Dcb*.nupkg" --source github --skip-duplicate --timeout 600
```
The glob `Alberto.Dcb*.nupkg` covers `Alberto.Dcb.Testing.*.nupkg` and `Alberto.Dcb.Testing.Xunit.*.nupkg` without further change.

## YAML parse check

```
publish-packages.yml parses
```

## dotnet build output (both projects, Release, 0 warnings)

```
Alberto.Dcb.Testing -> .../net9.0/Alberto.Dcb.Testing.dll
Alberto.Dcb.Testing -> .../net10.0/Alberto.Dcb.Testing.dll
Build succeeded.  0 Warning(s)  0 Error(s)

Alberto.Dcb.Testing.Xunit -> .../net9.0/Alberto.Dcb.Testing.Xunit.dll
Alberto.Dcb.Testing.Xunit -> .../net10.0/Alberto.Dcb.Testing.Xunit.dll
Build succeeded.  0 Warning(s)  0 Error(s)
```

## dotnet pack output

Both packs produced two files each (no errors, no build warnings — only the advisory "missing a readme" informational note, which also appears on the existing sibling packages and is not a build warning):

```
Alberto.Dcb.Testing.0.1.0-local.nupkg
Alberto.Dcb.Testing.0.1.0-local.snupkg
Alberto.Dcb.Testing.Xunit.0.1.0-local.nupkg
Alberto.Dcb.Testing.Xunit.0.1.0-local.snupkg
```

## .nuspec contents (verbatim)

### Alberto.Dcb.Testing.nuspec

```xml
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Alberto.Dcb.Testing</id>
    <version>0.1.0-local</version>
    <title>Alberto DCB Event Store - Testing Helpers</title>
    <authors>Bjorn Vandenbussche</authors>
    <license type="expression">MIT</license>
    <licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
    <projectUrl>https://github.com/codest-be/albertoo</projectUrl>
    <description>Test helpers for applications built on the Alberto DCB event store: an in-memory module harness, deterministic polling, an in-memory outbox store, and event construction helpers. Framework-neutral - takes no dependency on a test framework.</description>
    <copyright>Copyright © 2025 Alberto</copyright>
    <tags>DCB events eventsourcing dotnet postgresql dynamic-consistency-boundary</tags>
    <repository type="git" url="https://github.com/codest-be/albertoo" commit="d7ec7e6f0430db0e3b6a2a85278b328676bfd83a" />
    <dependencies>
      <group targetFramework="net10.0">
        <dependency id="Alberto.Dcb.InMemory" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="Alberto.Dcb.Messaging" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="Alberto.Dcb" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="Microsoft.Extensions.DependencyInjection.Abstractions" version="10.0.7" exclude="Build,Analyzers" />
        <dependency id="Microsoft.Extensions.Hosting" version="10.0.2" exclude="Build,Analyzers" />
      </group>
      <group targetFramework="net9.0">
        <dependency id="Alberto.Dcb.InMemory" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="Alberto.Dcb.Messaging" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="Alberto.Dcb" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="Microsoft.Extensions.DependencyInjection.Abstractions" version="10.0.7" exclude="Build,Analyzers" />
        <dependency id="Microsoft.Extensions.Hosting" version="10.0.2" exclude="Build,Analyzers" />
      </group>
    </dependencies>
  </metadata>
</package>
```

### Alberto.Dcb.Testing.Xunit.nuspec

```xml
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Alberto.Dcb.Testing.Xunit</id>
    <version>0.1.0-local</version>
    <title>Alberto DCB Event Store - Backend Conformance Suite</title>
    <authors>Bjorn Vandenbussche</authors>
    <license type="expression">MIT</license>
    <licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
    <projectUrl>https://github.com/codest-be/albertoo</projectUrl>
    <description>xUnit contract specifications for Alberto DCB backend implementations. Derive from these to run Alberto's own conformance suite against your event store, checkpoint store, state store, dead-letter store or outbox.</description>
    <copyright>Copyright © 2025 Alberto</copyright>
    <tags>DCB events eventsourcing dotnet postgresql dynamic-consistency-boundary</tags>
    <repository type="git" url="https://github.com/codest-be/albertoo" commit="d7ec7e6f0430db0e3b6a2a85278b328676bfd83a" />
    <dependencies>
      <group targetFramework="net10.0">
        <dependency id="Alberto.Dcb.Messaging" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="Alberto.Dcb.Testing" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="Alberto.Dcb" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="FluentAssertions" version="8.9.0" exclude="Build,Analyzers" />
        <dependency id="Microsoft.Extensions.TimeProvider.Testing" version="10.5.0" exclude="Build,Analyzers" />
        <dependency id="xunit.v3.assert" version="3.2.2" exclude="Build,Analyzers" />
        <dependency id="xunit.v3.extensibility.core" version="3.2.2" exclude="Build,Analyzers" />
      </group>
      <group targetFramework="net9.0">
        <dependency id="Alberto.Dcb.Messaging" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="Alberto.Dcb.Testing" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="Alberto.Dcb" version="0.1.0-local" exclude="Build,Analyzers" />
        <dependency id="FluentAssertions" version="8.9.0" exclude="Build,Analyzers" />
        <dependency id="Microsoft.Extensions.TimeProvider.Testing" version="10.5.0" exclude="Build,Analyzers" />
        <dependency id="xunit.v3.assert" version="3.2.2" exclude="Build,Analyzers" />
        <dependency id="xunit.v3.extensibility.core" version="3.2.2" exclude="Build,Analyzers" />
      </group>
    </dependencies>
  </metadata>
</package>
```

## Framework-neutrality verification

```
xunit check (no output = clean):
exit=1
```

`grep -i xunit` on `Alberto.Dcb.Testing.nuspec` returned no matches (exit 1). The package split is real: a consumer referencing only `Alberto.Dcb.Testing` gets no xunit transitive dependency.

## Test suite

```
Passed!  - Failed: 0, Passed: 1162, Skipped: 17, Total: 1179, Duration: 24s
```

Exactly matches the Task 7 baseline.

## Metadata conformance vs sibling packages

No conflicts found. All metadata (`Authors`, `License`, `ProjectUrl`, `Copyright`, `Tags`, `RepositoryUrl`, `RepositoryType`) flows from `Directory.Build.props` and is identical to the existing packable projects. The two new csproj files supply only `PackageId`, `Title`, `Description`, `IsPackable`, and `RootNamespace`, which is the same pattern the siblings use.

## Advisory note: missing readme

Both new packages emit the advisory "The package … is missing a readme." This is an informational NuGet best-practices note, not a build warning (`dotnet build` reported 0 warnings on both). The existing sibling packages emit the same advisory and no readme has been added to them. No action taken — consistent with siblings.

## Deviations from the brief

None. The brief's Steps 1–5 were executed verbatim and all expected outputs matched.

Step 6 (push and open PR) is a manual step outside this agent's scope.

## Uncertainties

None. All verifications passed on actual produced artifacts.
