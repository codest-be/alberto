# Benchmark Suite (Phases 1–2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the InMemory audit-era benchmarks with a Postgres-backed suite covering the Append and Query families, plus a tested comparison tool, committed profile-keyed baselines, and a nightly workflow.

**Architecture:** Four projects. `Alberto.Dcb.Benchmarks.Core` holds dependency-free logic (result schema, machine profile, comparer, deterministic event-plan generation) so it can be unit-tested without pulling BenchmarkDotNet into the test build. `Alberto.Dcb.Benchmarks` holds the BenchmarkDotNet harness and workloads, provisioning Postgres through Testcontainers with template-database cloning. `Alberto.Dcb.Benchmarks.Compare` is a thin CLI over Core. Results are committed JSON under `benchmarks/results/<profileId>/`.

**Tech Stack:** .NET 10, BenchmarkDotNet 0.14.0, Testcontainers.PostgreSql 4.11.0, Npgsql 10.0.2, System.Text.Json, xUnit v3 3.2.2, FluentAssertions 8.9.0.

**Source spec:** [2026-07-26-benchmark-suite-design.md](../specs/2026-07-26-benchmark-suite-design.md)

## Global Constraints

- **Target framework is `net10.0`** on every new project. `Directory.Build.props` does NOT set it globally — declare it per-csproj.
- **Central Package Management is on.** Every `<PackageReference>` MUST omit the `Version` attribute. A package not already in `Directory.Packages.props` must be added there first as `<PackageVersion Include="X" Version="Y" />`.
- **`Directory.Build.props` sets only documentation and packaging flags.** `Nullable`, `ImplicitUsings`, `TargetFramework`, and `TreatWarningsAsErrors` are per-project.
- **Never set `TreatWarningsAsErrors` on the BenchmarkDotNet project.** Its generated code raises CS8618/CS0649.
- **Exact package ids:** `Testcontainers.PostgreSql`, `DbUp-PostgreSQL`, `xunit.v3`, `BenchmarkDotNet`.
- **Pinned versions already present:** BenchmarkDotNet 0.14.0, Testcontainers.PostgreSql 4.11.0, Npgsql 10.0.2, xunit.v3 3.2.2, FluentAssertions 8.9.0, DbUp-PostgreSQL 7.0.1.
- **`AlbertoV3.slnx`** lists projects as `<Project Path="rel/path.csproj" />` inside `<Folder Name="/benchmarks/">`. Exclusion is by omission — there is no exclude element.
- **Test conventions:** file-scoped namespaces; sub-namespace mirrors the subdirectory. New test files here use **FluentAssertions with lowercase prose method names** (`Compose_joins_the_module_and_the_shard`). `using FluentAssertions;` and `using Xunit;` are both required — neither is in global usings. Never blend `Assert.*` and FluentAssertions in one file.
- **FluentAssertions exception style:** capture the lambda first (`var act = () => ...; act.Should().Throw<T>();`). Inline lambdas do not compile in this style.
- **Async tests return `Task`.** Never `async void`, never `ValueTask`.

---

## File Structure

| File | Responsibility |
|---|---|
| `benchmarks/Alberto.Dcb.Benchmarks.Core/MachineProfile.cs` | Captures and hashes the hardware/runtime identity a result is keyed by |
| `benchmarks/Alberto.Dcb.Benchmarks.Core/BenchmarkRun.cs` | The normalized result schema (`BenchmarkRun`, `RunMetadata`, `Measurement`) |
| `benchmarks/Alberto.Dcb.Benchmarks.Core/BdnImporter.cs` | Projects a BenchmarkDotNet full-JSON report into `BenchmarkRun` |
| `benchmarks/Alberto.Dcb.Benchmarks.Core/ResultComparer.cs` | Delta computation, thresholds, stddev banding, verdicts |
| `benchmarks/Alberto.Dcb.Benchmarks.Core/EventPlan.cs` | Deterministic event generation, pure and DB-free |
| `benchmarks/Alberto.Dcb.Benchmarks.Compare/Program.cs` | CLI: `--baseline`, `--candidate`, `--accept`, `--import` |
| `benchmarks/Alberto.Dcb.Benchmarks/Harness/BenchmarkDatabase.cs` | Container lifecycle, template build, per-class clone |
| `benchmarks/Alberto.Dcb.Benchmarks/Harness/BenchmarkConfig.cs` | Shared BDN job/exporter/diagnoser configuration |
| `benchmarks/Alberto.Dcb.Benchmarks/Workloads/AppendBenchmarks.cs` | Append family: shared base plus `AppendBenchmarks`, `BatchAppendBenchmarks`, `TagFanOutBenchmarks` |
| `benchmarks/Alberto.Dcb.Benchmarks/Workloads/QueryBenchmarks.cs` | Query family: shared base plus `QueryBenchmarks`, `MultiTagQueryBenchmarks` |
| `tests/Alberto.Dcb.Tests/Benchmarks/*.cs` | Unit tests for everything in Core |
| `.github/workflows/benchmarks.yml` | Nightly + dispatch run |

---

### Task 1: Core project and machine profile

**Files:**
- Create: `benchmarks/Alberto.Dcb.Benchmarks.Core/Alberto.Dcb.Benchmarks.Core.csproj`
- Create: `benchmarks/Alberto.Dcb.Benchmarks.Core/MachineProfile.cs`
- Test: `tests/Alberto.Dcb.Tests/Benchmarks/MachineProfileTests.cs`
- Modify: `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`
- Modify: `AlbertoV3.slnx`

**Interfaces:**
- Produces: `MachineProfile` record with `ProfileId` property; `MachineProfile.Capture()` static factory.

- [ ] **Step 1: Create the Core project**

Create `benchmarks/Alberto.Dcb.Benchmarks.Core/Alberto.Dcb.Benchmarks.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

</Project>
```

No `PackageReference` elements — this project deliberately depends on nothing but the BCL.

- [ ] **Step 2: Register it in the solution and reference it from tests**

In `AlbertoV3.slnx`, inside the existing `<Folder Name="/benchmarks/">` element, add:

```xml
    <Project Path="benchmarks/Alberto.Dcb.Benchmarks.Core/Alberto.Dcb.Benchmarks.Core.csproj" />
```

In `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`, add to the existing `ItemGroup` of `ProjectReference` elements:

```xml
    <ProjectReference Include="../../benchmarks/Alberto.Dcb.Benchmarks.Core/Alberto.Dcb.Benchmarks.Core.csproj" />
```

- [ ] **Step 3: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Benchmarks/MachineProfileTests.cs`:

```csharp
using Alberto.Dcb.Benchmarks.Core;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Benchmarks;

public class MachineProfileTests
{
    private static MachineProfile Sample(string label = "ci", int cores = 8) => new(
        Label: label,
        Os: "linux",
        Architecture: "X64",
        CpuModel: "AMD EPYC 7763",
        LogicalCores: cores,
        TotalMemoryBytes: 16_000_000_000L,
        DotnetVersion: "10.0.0",
        PostgresImage: "postgres:16-alpine",
        ExternalPostgres: false);

    [Fact]
    public void Identical_hardware_produces_an_identical_profile_id()
    {
        Sample().ProfileId.Should().Be(Sample().ProfileId);
    }

    [Fact]
    public void A_different_core_count_produces_a_different_profile_id()
    {
        Sample(cores: 8).ProfileId.Should().NotBe(Sample(cores: 16).ProfileId);
    }

    [Fact]
    public void The_profile_id_is_prefixed_with_the_label()
    {
        Sample(label: "ci").ProfileId.Should().StartWith("ci-");
    }

    [Fact]
    public void The_profile_id_is_filesystem_safe()
    {
        Sample().ProfileId.Should().MatchRegex("^[a-z0-9-]+$");
    }

    [Fact]
    public void Capture_reads_the_running_machine()
    {
        var profile = MachineProfile.Capture();

        profile.LogicalCores.Should().BeGreaterThan(0);
        profile.DotnetVersion.Should().NotBeNullOrWhiteSpace();
        profile.ProfileId.Should().NotBeNullOrWhiteSpace();
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~MachineProfileTests"`
Expected: FAIL — build error, `MachineProfile` does not exist.

- [ ] **Step 5: Implement MachineProfile**

Create `benchmarks/Alberto.Dcb.Benchmarks.Core/MachineProfile.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Alberto.Dcb.Benchmarks.Core;

/// <summary>
/// The hardware and runtime identity a benchmark result is keyed by.
///
/// Results are only ever compared within one profile. A laptop run diffed against a CI
/// baseline is worse than no trend line at all, because it looks like data — so
/// <see cref="ResultComparer"/> refuses to compare across differing <see cref="ProfileId"/>s.
/// </summary>
/// <param name="Label">
/// Environment label, from ALBERTO_BENCH_PROFILE_LABEL. "ci" on the runner, "local" otherwise.
/// It only makes directory names legible; the hash is what actually distinguishes machines.
/// </param>
public sealed record MachineProfile(
    string Label,
    string Os,
    string Architecture,
    string CpuModel,
    int LogicalCores,
    long TotalMemoryBytes,
    string DotnetVersion,
    string PostgresImage,
    bool ExternalPostgres)
{
    /// <summary>
    /// Stable directory-safe identity: the label plus a hash of every hardware field.
    /// Any field changing produces a different id, which is the point — it forces a new
    /// baseline rather than silently comparing across machines.
    /// </summary>
    public string ProfileId
    {
        get
        {
            var canonical = string.Join(
                '|',
                Os,
                Architecture,
                CpuModel,
                LogicalCores.ToString(),
                TotalMemoryBytes.ToString(),
                DotnetVersion,
                PostgresImage,
                ExternalPostgres ? "external" : "container");

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            var suffix = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();

            return $"{Slug(Label)}-{suffix}";
        }
    }

    /// <summary>Reads the profile of the machine this process is running on.</summary>
    public static MachineProfile Capture(string? postgresImage = null, bool externalPostgres = false) => new(
        Label: Environment.GetEnvironmentVariable("ALBERTO_BENCH_PROFILE_LABEL") ?? "local",
        Os: OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsWindows() ? "windows"
            : "unknown",
        Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
        CpuModel: NormalizeCpu(),
        LogicalCores: Environment.ProcessorCount,
        TotalMemoryBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        DotnetVersion: Environment.Version.ToString(),
        PostgresImage: postgresImage ?? "unknown",
        ExternalPostgres: externalPostgres);

    // RuntimeInformation exposes no CPU model, so this is the closest portable stand-in.
    // It is part of the hash, not display copy, so precision matters less than stability.
    private static string NormalizeCpu()
        => RuntimeInformation.ProcessArchitecture + "/" + RuntimeInformation.OSArchitecture;

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();

        return new string(chars).Trim('-');
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~MachineProfileTests"`
Expected: PASS, 5 tests.

- [ ] **Step 7: Commit**

```bash
git add benchmarks/Alberto.Dcb.Benchmarks.Core tests/Alberto.Dcb.Tests AlbertoV3.slnx && git commit -m "feat(benchmarks): machine profile identity for result keying"
```

---

### Task 2: Result schema

**Files:**
- Create: `benchmarks/Alberto.Dcb.Benchmarks.Core/BenchmarkRun.cs`
- Test: `tests/Alberto.Dcb.Tests/Benchmarks/BenchmarkRunTests.cs`

**Interfaces:**
- Consumes: `MachineProfile` (Task 1).
- Produces: `BenchmarkRun`, `RunMetadata`, `Measurement` records; `Measurement.Key`; `BenchmarkRun.ToJson()` and `BenchmarkRun.FromJson(string)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Benchmarks/BenchmarkRunTests.cs`:

```csharp
using Alberto.Dcb.Benchmarks.Core;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Benchmarks;

public class BenchmarkRunTests
{
    private static Measurement Measure(string id, params (string Key, string Value)[] parameters) => new(
        Id: id,
        Params: parameters.ToDictionary(p => p.Key, p => p.Value),
        MeanNs: 1200.0,
        StdDevNs: 40.0,
        OpsPerSec: 833_333.0,
        AllocatedBytes: 512);

    [Fact]
    public void A_measurement_key_combines_the_id_and_its_parameters()
    {
        var measurement = Measure("Append.Batch", ("BatchSize", "100"), ("StoreSize", "10000"));

        measurement.Key.Should().Be("Append.Batch[BatchSize=100,StoreSize=10000]");
    }

    [Fact]
    public void Measurement_keys_are_stable_regardless_of_parameter_ordering()
    {
        var forward = Measure("Append.Batch", ("BatchSize", "100"), ("StoreSize", "10000"));
        var reversed = Measure("Append.Batch", ("StoreSize", "10000"), ("BatchSize", "100"));

        forward.Key.Should().Be(reversed.Key);
    }

    [Fact]
    public void A_measurement_without_parameters_keys_on_its_id_alone()
    {
        Measure("Query.StreamAll").Key.Should().Be("Query.StreamAll");
    }

    [Fact]
    public void A_run_round_trips_through_json()
    {
        var original = new BenchmarkRun(
            SchemaVersion: 1,
            Run: new RunMetadata("2026-07-26T02:12:00Z", "a1b2c3d", "ci-3f2a91b8", "0.1.0"),
            Measurements: [Measure("Append.Single"), Measure("Query.StreamAll")]);

        var restored = BenchmarkRun.FromJson(original.ToJson());

        restored.SchemaVersion.Should().Be(1);
        restored.Run.ProfileId.Should().Be("ci-3f2a91b8");
        restored.Run.GitSha.Should().Be("a1b2c3d");
        restored.Measurements.Should().HaveCount(2);
        restored.Measurements[0].MeanNs.Should().Be(1200.0);
        restored.Measurements[0].AllocatedBytes.Should().Be(512);
    }

    [Fact]
    public void Json_is_written_indented_so_diffs_are_readable()
    {
        var run = new BenchmarkRun(
            SchemaVersion: 1,
            Run: new RunMetadata("2026-07-26T02:12:00Z", "a1b2c3d", "ci-3f2a91b8", "0.1.0"),
            Measurements: [Measure("Append.Single")]);

        run.ToJson().Should().Contain("\n  ");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~BenchmarkRunTests"`
Expected: FAIL — `BenchmarkRun` does not exist.

- [ ] **Step 3: Implement the schema**

Create `benchmarks/Alberto.Dcb.Benchmarks.Core/BenchmarkRun.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alberto.Dcb.Benchmarks.Core;

/// <summary>Provenance for one benchmark run.</summary>
public sealed record RunMetadata(
    string Timestamp,
    string GitSha,
    string ProfileId,
    string AlbertoVersion);

/// <summary>
/// One measured case. Every producer — the BenchmarkDotNet importer, the macro throughput
/// harness, and (later) the Marten parity harness — projects into this shape, which is what
/// lets a single comparer handle all of them.
/// </summary>
public sealed record Measurement(
    string Id,
    IReadOnlyDictionary<string, string> Params,
    double MeanNs,
    double StdDevNs,
    double OpsPerSec,
    long AllocatedBytes)
{
    /// <summary>
    /// Identity used to pair a candidate measurement with its baseline. Parameters are sorted
    /// so a reordering in the producer does not orphan the whole history.
    /// </summary>
    [JsonIgnore]
    public string Key
    {
        get
        {
            if (Params.Count == 0)
            {
                return Id;
            }

            var parts = Params
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={p.Value}");

            return $"{Id}[{string.Join(',', parts)}]";
        }
    }
}

/// <summary>A complete run: provenance plus every measurement it produced.</summary>
public sealed record BenchmarkRun(
    int SchemaVersion,
    RunMetadata Run,
    IReadOnlyList<Measurement> Measurements)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static BenchmarkRun FromJson(string json)
        => JsonSerializer.Deserialize<BenchmarkRun>(json, Options)
           ?? throw new InvalidOperationException("Benchmark result JSON deserialized to null.");
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~BenchmarkRunTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Alberto.Dcb.Benchmarks.Core tests/Alberto.Dcb.Tests && git commit -m "feat(benchmarks): normalized result schema"
```

---

### Task 3: BenchmarkDotNet report importer

**Files:**
- Create: `benchmarks/Alberto.Dcb.Benchmarks.Core/BdnImporter.cs`
- Test: `tests/Alberto.Dcb.Tests/Benchmarks/BdnImporterTests.cs`

**Interfaces:**
- Consumes: `Measurement`, `BenchmarkRun`, `RunMetadata` (Task 2).
- Produces: `BdnImporter.Import(string bdnJson, RunMetadata metadata) → BenchmarkRun`.

**Note on the fixture:** the JSON below matches BenchmarkDotNet 0.14.0's `*-report-full.json`. Task 10 regenerates this fixture from a real run and the test is re-asserted against it — if 0.14.0's actual shape differs, that is where it surfaces and gets corrected.

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Benchmarks/BdnImporterTests.cs`:

```csharp
using Alberto.Dcb.Benchmarks.Core;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Benchmarks;

public class BdnImporterTests
{
    private const string ReportJson = """
    {
      "Title": "AppendBenchmarks",
      "Benchmarks": [
        {
          "Namespace": "Alberto.Dcb.Benchmarks.Workloads",
          "Type": "AppendBenchmarks",
          "Method": "BatchAppend",
          "Parameters": "BatchSize=100, StoreSize=10000",
          "Statistics": { "Mean": 125000.5, "StandardDeviation": 4200.25 },
          "Memory": { "BytesAllocatedPerOperation": 8192 }
        },
        {
          "Namespace": "Alberto.Dcb.Benchmarks.Workloads",
          "Type": "AppendBenchmarks",
          "Method": "SingleAppend",
          "Parameters": "",
          "Statistics": { "Mean": 2000.0, "StandardDeviation": 50.0 },
          "Memory": { "BytesAllocatedPerOperation": 512 }
        }
      ]
    }
    """;

    private static readonly RunMetadata Metadata =
        new("2026-07-26T02:12:00Z", "a1b2c3d", "ci-3f2a91b8", "0.1.0");

    [Fact]
    public void Every_benchmark_becomes_a_measurement()
    {
        BdnImporter.Import(ReportJson, Metadata).Measurements.Should().HaveCount(2);
    }

    [Fact]
    public void The_measurement_id_is_the_type_and_method()
    {
        var run = BdnImporter.Import(ReportJson, Metadata);

        run.Measurements[0].Id.Should().Be("AppendBenchmarks.BatchAppend");
    }

    [Fact]
    public void Parameters_are_split_into_a_dictionary()
    {
        var measurement = BdnImporter.Import(ReportJson, Metadata).Measurements[0];

        measurement.Params.Should().HaveCount(2);
        measurement.Params["BatchSize"].Should().Be("100");
        measurement.Params["StoreSize"].Should().Be("10000");
    }

    [Fact]
    public void An_empty_parameter_string_yields_no_parameters()
    {
        BdnImporter.Import(ReportJson, Metadata).Measurements[1].Params.Should().BeEmpty();
    }

    [Fact]
    public void Statistics_and_allocations_are_carried_across()
    {
        var measurement = BdnImporter.Import(ReportJson, Metadata).Measurements[0];

        measurement.MeanNs.Should().Be(125000.5);
        measurement.StdDevNs.Should().Be(4200.25);
        measurement.AllocatedBytes.Should().Be(8192);
    }

    [Fact]
    public void Ops_per_second_is_derived_from_the_mean()
    {
        var measurement = BdnImporter.Import(ReportJson, Metadata).Measurements[1];

        // 2000 ns per op => 500,000 ops/sec
        measurement.OpsPerSec.Should().BeApproximately(500_000.0, 0.01);
    }

    [Fact]
    public void A_report_with_no_benchmarks_is_rejected()
    {
        var act = () => BdnImporter.Import("""{ "Benchmarks": [] }""", Metadata);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no benchmarks*");
    }

    // A run over several benchmark classes writes one report per class, so a full nightly
    // run must merge them into a single result document.

    private const string SecondReportJson = """
    {
      "Title": "QueryBenchmarks",
      "Benchmarks": [
        {
          "Type": "QueryBenchmarks",
          "Method": "TailRead",
          "Parameters": "StoreSize=10000",
          "Statistics": { "Mean": 900.0, "StandardDeviation": 10.0 },
          "Memory": { "BytesAllocatedPerOperation": 256 }
        }
      ]
    }
    """;

    [Fact]
    public void Several_reports_merge_into_one_run()
    {
        var run = BdnImporter.ImportMany([ReportJson, SecondReportJson], Metadata);

        run.Measurements.Should().HaveCount(3);
        run.Measurements.Should().Contain(m => m.Id == "QueryBenchmarks.TailRead");
        run.Measurements.Should().Contain(m => m.Id == "AppendBenchmarks.SingleAppend");
    }

    [Fact]
    public void Merging_reports_that_share_a_measurement_key_is_rejected()
    {
        // Silently keeping one would make the comparer's key lookup ambiguous and quietly
        // drop a benchmark from the baseline.
        var act = () => BdnImporter.ImportMany([ReportJson, ReportJson], Metadata);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate*");
    }

    [Fact]
    public void Merging_no_reports_is_rejected()
    {
        var act = () => BdnImporter.ImportMany([], Metadata);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no benchmarks*");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~BdnImporterTests"`
Expected: FAIL — `BdnImporter` does not exist.

- [ ] **Step 3: Implement the importer**

Create `benchmarks/Alberto.Dcb.Benchmarks.Core/BdnImporter.cs`:

```csharp
using System.Text.Json;

namespace Alberto.Dcb.Benchmarks.Core;

/// <summary>
/// Projects BenchmarkDotNet's full JSON report into the normalized <see cref="BenchmarkRun"/>.
///
/// BDN's own schema is a reporting format, not a stable contract, so this is the single
/// place that knows its shape. Everything downstream sees only normalized measurements.
/// </summary>
public static class BdnImporter
{
    public static BenchmarkRun Import(string bdnJson, RunMetadata metadata)
        => ImportMany([bdnJson], metadata);

    /// <summary>
    /// Merges several reports into one run. A full nightly run spans multiple benchmark
    /// classes and BenchmarkDotNet writes one report per class, so merging is the normal
    /// path — importing a single report is the special case.
    /// </summary>
    public static BenchmarkRun ImportMany(IEnumerable<string> bdnJsonDocuments, RunMetadata metadata)
    {
        var measurements = new List<Measurement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bdnJson in bdnJsonDocuments)
        {
            using var document = JsonDocument.Parse(bdnJson);

            if (!document.RootElement.TryGetProperty("Benchmarks", out var benchmarks)
                || benchmarks.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var benchmark in benchmarks.EnumerateArray())
            {
                var measurement = ToMeasurement(benchmark);

                // Two measurements sharing a key would make the comparer's lookup ambiguous
                // and silently drop one from the baseline.
                if (!seen.Add(measurement.Key))
                {
                    throw new InvalidOperationException(
                        $"Duplicate measurement key '{measurement.Key}' across BenchmarkDotNet reports.");
                }

                measurements.Add(measurement);
            }
        }

        if (measurements.Count == 0)
        {
            throw new InvalidOperationException(
                "BenchmarkDotNet reports contained no benchmarks. The run produced no results.");
        }

        return new BenchmarkRun(BenchmarkRun.CurrentSchemaVersion, metadata, measurements);
    }

    private static Measurement ToMeasurement(JsonElement benchmark)
    {
        var type = benchmark.GetProperty("Type").GetString() ?? "Unknown";
        var method = benchmark.GetProperty("Method").GetString() ?? "Unknown";

        var statistics = benchmark.GetProperty("Statistics");
        var meanNs = statistics.GetProperty("Mean").GetDouble();
        var stdDevNs = statistics.TryGetProperty("StandardDeviation", out var sd) ? sd.GetDouble() : 0.0;

        var allocated = 0L;
        if (benchmark.TryGetProperty("Memory", out var memory)
            && memory.ValueKind == JsonValueKind.Object
            && memory.TryGetProperty("BytesAllocatedPerOperation", out var bytes)
            && bytes.ValueKind == JsonValueKind.Number)
        {
            allocated = bytes.GetInt64();
        }

        var parameters = ParseParameters(
            benchmark.TryGetProperty("Parameters", out var p) ? p.GetString() : null);

        // A zero mean would be a degenerate result; guard rather than divide by zero.
        var opsPerSec = meanNs > 0 ? 1_000_000_000.0 / meanNs : 0.0;

        return new Measurement($"{type}.{method}", parameters, meanNs, stdDevNs, opsPerSec, allocated);
    }

    /// <summary>BDN renders parameters as a flat "Name=Value, Name=Value" string.</summary>
    private static Dictionary<string, string> ParseParameters(string? parameters)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(parameters))
        {
            return result;
        }

        foreach (var pair in parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            result[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
        }

        return result;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~BdnImporterTests"`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Alberto.Dcb.Benchmarks.Core tests/Alberto.Dcb.Tests && git commit -m "feat(benchmarks): import BenchmarkDotNet reports into the normalized schema"
```

---

### Task 4: Result comparer

This is the task that matters most. It is the only thing standing between a real regression and a false all-clear.

**Files:**
- Create: `benchmarks/Alberto.Dcb.Benchmarks.Core/ResultComparer.cs`
- Test: `tests/Alberto.Dcb.Tests/Benchmarks/ResultComparerTests.cs`

**Interfaces:**
- Consumes: `BenchmarkRun`, `Measurement`, `RunMetadata` (Task 2).
- Produces: `Thresholds`, `ComparisonVerdict`, `MeasurementDelta`, `ComparisonReport`, `ResultComparer.Compare(baseline, candidate, thresholds)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Benchmarks/ResultComparerTests.cs`:

```csharp
using Alberto.Dcb.Benchmarks.Core;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Benchmarks;

public class ResultComparerTests
{
    private const string Profile = "ci-3f2a91b8";

    private static Measurement Measure(
        string id, double meanNs, double stdDevNs = 1.0, long allocated = 500) =>
        new(id, new Dictionary<string, string>(), meanNs, stdDevNs, 0.0, allocated);

    private static BenchmarkRun Run(string profileId, params Measurement[] measurements) =>
        new(1, new RunMetadata("2026-07-26T02:12:00Z", "sha", profileId, "0.1.0"), measurements);

    [Fact]
    public void Comparing_across_machine_profiles_is_refused()
    {
        var baseline = Run("ci-3f2a91b8", Measure("A", 100));
        var candidate = Run("local-9f3e0011", Measure("A", 100));

        var act = () => ResultComparer.Compare(baseline, candidate, Thresholds.Default);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*different machine profiles*");
    }

    [Fact]
    public void An_unchanged_measurement_is_reported_as_unchanged()
    {
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100)),
            Run(Profile, Measure("A", 101)),
            Thresholds.Default);

        report.Deltas.Should().ContainSingle()
            .Which.Verdict.Should().Be(ComparisonVerdict.Unchanged);
        report.HasRegression.Should().BeFalse();
    }

    [Fact]
    public void A_mean_increase_beyond_the_threshold_and_outside_the_noise_band_regresses()
    {
        // +50% mean, and the 50ns delta dwarfs the 2ns combined stddev band.
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100, stdDevNs: 1.0)),
            Run(Profile, Measure("A", 150, stdDevNs: 1.0)),
            Thresholds.Default);

        report.Deltas.Should().ContainSingle()
            .Which.Verdict.Should().Be(ComparisonVerdict.Regressed);
        report.HasRegression.Should().BeTrue();
    }

    [Fact]
    public void A_mean_increase_inside_the_noise_band_does_not_regress()
    {
        // +50% mean, but the run is so noisy that the delta sits inside the combined stddev.
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100, stdDevNs: 40.0)),
            Run(Profile, Measure("A", 150, stdDevNs: 40.0)),
            Thresholds.Default);

        report.Deltas.Should().ContainSingle()
            .Which.Verdict.Should().Be(ComparisonVerdict.Unchanged);
        report.HasRegression.Should().BeFalse();
    }

    [Fact]
    public void A_faster_measurement_is_reported_as_improved()
    {
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 200, stdDevNs: 1.0)),
            Run(Profile, Measure("A", 100, stdDevNs: 1.0)),
            Thresholds.Default);

        report.Deltas.Should().ContainSingle()
            .Which.Verdict.Should().Be(ComparisonVerdict.Improved);
        report.HasRegression.Should().BeFalse();
    }

    [Fact]
    public void An_allocation_increase_beyond_its_tighter_threshold_regresses()
    {
        // Mean is unchanged; allocations grow 20% against a 10% gate.
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100, allocated: 1000)),
            Run(Profile, Measure("A", 100, allocated: 1200)),
            Thresholds.Default);

        report.Deltas.Should().ContainSingle()
            .Which.Verdict.Should().Be(ComparisonVerdict.Regressed);
    }

    [Fact]
    public void Allocation_growth_is_not_excused_by_the_noise_band()
    {
        // Allocation counts are near-deterministic, so no stddev gate applies to them.
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100, stdDevNs: 500.0, allocated: 1000)),
            Run(Profile, Measure("A", 100, stdDevNs: 500.0, allocated: 1200)),
            Thresholds.Default);

        report.Deltas.Should().ContainSingle()
            .Which.Verdict.Should().Be(ComparisonVerdict.Regressed);
    }

    [Fact]
    public void A_new_measurement_is_reported_but_never_fails_the_run()
    {
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100)),
            Run(Profile, Measure("A", 100), Measure("B", 100)),
            Thresholds.Default);

        report.Deltas.Should().Contain(d => d.Key == "B" && d.Verdict == ComparisonVerdict.Added);
        report.HasRegression.Should().BeFalse();
    }

    [Fact]
    public void A_removed_measurement_is_reported_but_never_fails_the_run()
    {
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100), Measure("B", 100)),
            Run(Profile, Measure("A", 100)),
            Thresholds.Default);

        report.Deltas.Should().Contain(d => d.Key == "B" && d.Verdict == ComparisonVerdict.Removed);
        report.HasRegression.Should().BeFalse();
    }

    [Fact]
    public void Deltas_are_ordered_worst_first()
    {
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100, stdDevNs: 1.0), Measure("B", 100, stdDevNs: 1.0)),
            Run(Profile, Measure("A", 105, stdDevNs: 1.0), Measure("B", 300, stdDevNs: 1.0)),
            Thresholds.Default);

        report.Deltas[0].Key.Should().Be("B");
    }

    [Fact]
    public void The_default_thresholds_are_twenty_percent_mean_and_ten_percent_allocations()
    {
        Thresholds.Default.MeanPercent.Should().Be(20.0);
        Thresholds.Default.AllocatedPercent.Should().Be(10.0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~ResultComparerTests"`
Expected: FAIL — `ResultComparer` does not exist.

- [ ] **Step 3: Implement the comparer**

Create `benchmarks/Alberto.Dcb.Benchmarks.Core/ResultComparer.cs`:

```csharp
namespace Alberto.Dcb.Benchmarks.Core;

/// <summary>
/// Regression gates. Deliberately asymmetric: Postgres in a container on a shared runner is a
/// noisy instrument for timing, while allocation counts stay near-deterministic — so the
/// allocation gate is tighter and is not excused by the noise band.
/// </summary>
public sealed record Thresholds(double MeanPercent, double AllocatedPercent)
{
    public static Thresholds Default { get; } = new(MeanPercent: 20.0, AllocatedPercent: 10.0);
}

public enum ComparisonVerdict
{
    Unchanged,
    Improved,
    Regressed,
    Added,
    Removed,
}

public sealed record MeasurementDelta(
    string Key,
    double? BaselineMeanNs,
    double? CandidateMeanNs,
    double? MeanPercent,
    long? BaselineAllocatedBytes,
    long? CandidateAllocatedBytes,
    double? AllocatedPercent,
    ComparisonVerdict Verdict);

public sealed record ComparisonReport(IReadOnlyList<MeasurementDelta> Deltas)
{
    public bool HasRegression => Deltas.Any(d => d.Verdict == ComparisonVerdict.Regressed);
}

public static class ResultComparer
{
    public static ComparisonReport Compare(
        BenchmarkRun baseline,
        BenchmarkRun candidate,
        Thresholds thresholds)
    {
        // A laptop run diffed against a CI baseline looks like data but is not. Refuse
        // rather than warn — a warning in a log is a warning nobody reads.
        if (!string.Equals(baseline.Run.ProfileId, candidate.Run.ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to compare results from different machine profiles: "
                + $"baseline '{baseline.Run.ProfileId}' vs candidate '{candidate.Run.ProfileId}'.");
        }

        var baselineByKey = baseline.Measurements.ToDictionary(m => m.Key, StringComparer.Ordinal);
        var candidateByKey = candidate.Measurements.ToDictionary(m => m.Key, StringComparer.Ordinal);

        var deltas = new List<MeasurementDelta>();

        foreach (var (key, candidateMeasurement) in candidateByKey)
        {
            if (baselineByKey.TryGetValue(key, out var baselineMeasurement))
            {
                deltas.Add(Evaluate(key, baselineMeasurement, candidateMeasurement, thresholds));
            }
            else
            {
                deltas.Add(new MeasurementDelta(
                    key, null, candidateMeasurement.MeanNs, null,
                    null, candidateMeasurement.AllocatedBytes, null,
                    ComparisonVerdict.Added));
            }
        }

        foreach (var (key, baselineMeasurement) in baselineByKey)
        {
            if (!candidateByKey.ContainsKey(key))
            {
                deltas.Add(new MeasurementDelta(
                    key, baselineMeasurement.MeanNs, null, null,
                    baselineMeasurement.AllocatedBytes, null, null,
                    ComparisonVerdict.Removed));
            }
        }

        // Worst first, so a truncated report still shows what matters.
        var ordered = deltas
            .OrderByDescending(d => d.Verdict == ComparisonVerdict.Regressed)
            .ThenByDescending(d => d.MeanPercent ?? double.MinValue)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .ToList();

        return new ComparisonReport(ordered);
    }

    private static MeasurementDelta Evaluate(
        string key, Measurement baseline, Measurement candidate, Thresholds thresholds)
    {
        var meanPercent = Percent(baseline.MeanNs, candidate.MeanNs);
        var allocatedPercent = Percent(baseline.AllocatedBytes, candidate.AllocatedBytes);

        // Timing must clear BOTH the percentage gate and the combined noise band. On a shared
        // runner the band is what stops a slow neighbour from being reported as a regression.
        var noiseBand = baseline.StdDevNs + candidate.StdDevNs;
        var meanRegressed =
            meanPercent > thresholds.MeanPercent
            && (candidate.MeanNs - baseline.MeanNs) > noiseBand;

        // Allocations get no noise band — they do not drift between runs.
        var allocationRegressed = allocatedPercent > thresholds.AllocatedPercent;

        var verdict = meanRegressed || allocationRegressed
            ? ComparisonVerdict.Regressed
            : meanPercent < -thresholds.MeanPercent
                ? ComparisonVerdict.Improved
                : ComparisonVerdict.Unchanged;

        return new MeasurementDelta(
            key,
            baseline.MeanNs,
            candidate.MeanNs,
            meanPercent,
            baseline.AllocatedBytes,
            candidate.AllocatedBytes,
            allocatedPercent,
            verdict);
    }

    private static double Percent(double baseline, double candidate)
        => baseline == 0 ? 0 : (candidate - baseline) / baseline * 100.0;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~ResultComparerTests"`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Alberto.Dcb.Benchmarks.Core tests/Alberto.Dcb.Tests && git commit -m "feat(benchmarks): result comparer with asymmetric thresholds and noise banding"
```

---

### Task 5: Deterministic event plan

**Files:**
- Create: `benchmarks/Alberto.Dcb.Benchmarks.Core/EventPlan.cs`
- Test: `tests/Alberto.Dcb.Tests/Benchmarks/EventPlanTests.cs`
- Modify: `benchmarks/Alberto.Dcb.Benchmarks.Core/Alberto.Dcb.Benchmarks.Core.csproj`

**Interfaces:**
- Produces: `EventPlan.Build(int count, int seed) → IReadOnlyList<EventToPersist>`; constants `EventPlan.TypeIds`, `EventPlan.DistinctOrders`.

Generation is a pure function so seeding determinism is testable without a database. If the plan drifts, today's reseeded template stops being comparable to yesterday's, and the whole trend line is quietly invalid.

- [ ] **Step 1: Add the Alberto.Dcb reference to Core**

`EventPlan` returns real `EventToPersist` instances, so Core needs the core library. Edit `benchmarks/Alberto.Dcb.Benchmarks.Core/Alberto.Dcb.Benchmarks.Core.csproj` and add before `</Project>`:

```xml
  <ItemGroup>
    <ProjectReference Include="../../src/Alberto.Dcb/Alberto.Dcb.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Benchmarks/EventPlanTests.cs`:

```csharp
using Alberto.Dcb.Benchmarks.Core;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Benchmarks;

public class EventPlanTests
{
    [Fact]
    public void The_plan_contains_exactly_the_requested_number_of_events()
    {
        EventPlan.Build(count: 500, seed: 42).Should().HaveCount(500);
    }

    [Fact]
    public void The_same_seed_produces_an_identical_plan()
    {
        var first = EventPlan.Build(count: 1000, seed: 42);
        var second = EventPlan.Build(count: 1000, seed: 42);

        first.Select(e => e.EventType.Id)
            .Should().Equal(second.Select(e => e.EventType.Id));

        first.SelectMany(e => e.Tags).Select(t => t.Value)
            .Should().Equal(second.SelectMany(e => e.Tags).Select(t => t.Value));
    }

    [Fact]
    public void A_different_seed_produces_a_different_type_distribution()
    {
        var first = EventPlan.Build(count: 1000, seed: 42).Select(e => e.EventType.Id);
        var second = EventPlan.Build(count: 1000, seed: 43).Select(e => e.EventType.Id);

        first.Should().NotEqual(second);
    }

    [Fact]
    public void Events_are_spread_across_the_declared_event_types()
    {
        var types = EventPlan.Build(count: 1000, seed: 42)
            .Select(e => e.EventType.Id)
            .Distinct()
            .ToList();

        types.Should().BeEquivalentTo(EventPlan.TypeIds);
    }

    [Fact]
    public void Tags_fan_out_across_the_declared_number_of_orders()
    {
        var tags = EventPlan.Build(count: 1000, seed: 42)
            .SelectMany(e => e.Tags)
            .Select(t => t.Value)
            .Distinct()
            .ToList();

        tags.Should().HaveCount(EventPlan.DistinctOrders);
    }

    [Fact]
    public void Every_event_carries_exactly_one_tag()
    {
        EventPlan.Build(count: 200, seed: 42).Should().OnlyContain(e => e.Tags.Count == 1);
    }

    [Fact]
    public void Every_event_carries_non_empty_json_data()
    {
        EventPlan.Build(count: 200, seed: 42)
            .Should().OnlyContain(e => e.EventData.StartsWith("{") && e.EventData.EndsWith("}"));
    }

    [Fact]
    public void A_negative_count_is_rejected()
    {
        var act = () => EventPlan.Build(count: -1, seed: 42);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~EventPlanTests"`
Expected: FAIL — `EventPlan` does not exist.

- [ ] **Step 4: Implement EventPlan**

Create `benchmarks/Alberto.Dcb.Benchmarks.Core/EventPlan.cs`:

```csharp
namespace Alberto.Dcb.Benchmarks.Core;

/// <summary>
/// Deterministic event generation for benchmark seeding.
///
/// Pure and database-free on purpose: seeding determinism is the property that makes today's
/// reseeded template comparable to yesterday's, and it is only testable cheaply if generation
/// does not need a Postgres.
/// </summary>
public static class EventPlan
{
    /// <summary>The event types seeded stores are built from.</summary>
    public static IReadOnlyList<string> TypeIds { get; } =
        ["order-placed", "order-cancelled", "payment-received"];

    /// <summary>Distinct order tags. Models the tag fan-out of a busy service.</summary>
    public const int DistinctOrders = 100;

    public static IReadOnlyList<EventToPersist> Build(int count, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var random = new Random(seed);
        var types = TypeIds.Select(id => new EventType(id)).ToArray();
        var events = new EventToPersist[count];

        for (var i = 0; i < count; i++)
        {
            var type = types[random.Next(types.Length)];
            var orderId = (i % DistinctOrders + 1).ToString();

            events[i] = new EventToPersist
            {
                EventType = type,
                // FromStorage skips the validation regex. These ids are generated, not
                // user-supplied, and seeding cost is not what the suite is measuring.
                Tags = [EventTag.FromStorage("order", orderId)],
                EventData = $$"""{"orderId":"{{orderId}}","seq":{{i}},"amount":9.99}""",
            };
        }

        return events;
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~EventPlanTests"`
Expected: PASS, 8 tests.

- [ ] **Step 6: Commit**

```bash
git add benchmarks/Alberto.Dcb.Benchmarks.Core tests/Alberto.Dcb.Tests && git commit -m "feat(benchmarks): deterministic event plan generation"
```

---

### Task 6: Compare CLI

**Files:**
- Create: `benchmarks/Alberto.Dcb.Benchmarks.Compare/Alberto.Dcb.Benchmarks.Compare.csproj`
- Create: `benchmarks/Alberto.Dcb.Benchmarks.Compare/Program.cs`
- Create: `benchmarks/Alberto.Dcb.Benchmarks.Compare/ReportRenderer.cs`
- Test: `tests/Alberto.Dcb.Tests/Benchmarks/ReportRendererTests.cs`
- Modify: `AlbertoV3.slnx`

**Interfaces:**
- Consumes: `ComparisonReport`, `MeasurementDelta`, `ComparisonVerdict`, `BenchmarkRun`, `BdnImporter`, `Thresholds` (Tasks 2–4).
- Produces: `ReportRenderer.ToMarkdown(ComparisonReport) → string`.

Argument parsing is hand-rolled rather than using System.CommandLine. Four flags do not justify a dependency, and the tool must stay trivially runnable from a workflow step.

- [ ] **Step 1: Write the failing test for the renderer**

Create `tests/Alberto.Dcb.Tests/Benchmarks/ReportRendererTests.cs`:

```csharp
using Alberto.Dcb.Benchmarks.Compare;
using Alberto.Dcb.Benchmarks.Core;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Benchmarks;

public class ReportRendererTests
{
    private static MeasurementDelta Delta(string key, ComparisonVerdict verdict, double meanPercent) =>
        new(key, 100.0, 100.0 + meanPercent, meanPercent, 500, 500, 0.0, verdict);

    [Fact]
    public void The_table_has_a_header_row()
    {
        var markdown = ReportRenderer.ToMarkdown(
            new ComparisonReport([Delta("A", ComparisonVerdict.Unchanged, 1.0)]));

        markdown.Should().Contain("| Benchmark |");
        markdown.Should().Contain("| --- |");
    }

    [Fact]
    public void Each_delta_becomes_a_row()
    {
        var markdown = ReportRenderer.ToMarkdown(new ComparisonReport([
            Delta("Append.Single", ComparisonVerdict.Regressed, 30.0),
            Delta("Query.StreamAll", ComparisonVerdict.Unchanged, 1.0),
        ]));

        markdown.Should().Contain("Append.Single");
        markdown.Should().Contain("Query.StreamAll");
    }

    [Fact]
    public void A_regression_is_marked_so_it_is_visible_at_a_glance()
    {
        var markdown = ReportRenderer.ToMarkdown(
            new ComparisonReport([Delta("A", ComparisonVerdict.Regressed, 30.0)]));

        markdown.Should().Contain("REGRESSED");
    }

    [Fact]
    public void Percentages_are_signed_so_direction_is_unambiguous()
    {
        var markdown = ReportRenderer.ToMarkdown(
            new ComparisonReport([Delta("A", ComparisonVerdict.Regressed, 30.0)]));

        markdown.Should().Contain("+30.0%");
    }

    [Fact]
    public void An_added_benchmark_renders_without_a_baseline_figure()
    {
        var added = new MeasurementDelta("New", null, 100.0, null, null, 500, null, ComparisonVerdict.Added);

        var markdown = ReportRenderer.ToMarkdown(new ComparisonReport([added]));

        markdown.Should().Contain("New");
        markdown.Should().Contain("ADDED");
    }

    [Fact]
    public void An_empty_report_still_renders_a_table()
    {
        var markdown = ReportRenderer.ToMarkdown(new ComparisonReport([]));

        markdown.Should().Contain("| Benchmark |");
    }
}
```

- [ ] **Step 2: Create the Compare project and register it**

Create `benchmarks/Alberto.Dcb.Benchmarks.Compare/Alberto.Dcb.Benchmarks.Compare.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>Alberto.Dcb.Benchmarks.Compare</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../Alberto.Dcb.Benchmarks.Core/Alberto.Dcb.Benchmarks.Core.csproj" />
  </ItemGroup>

</Project>
```

In `AlbertoV3.slnx`, inside `<Folder Name="/benchmarks/">`, add:

```xml
    <Project Path="benchmarks/Alberto.Dcb.Benchmarks.Compare/Alberto.Dcb.Benchmarks.Compare.csproj" />
```

In `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`, add to the `ProjectReference` group:

```xml
    <ProjectReference Include="../../benchmarks/Alberto.Dcb.Benchmarks.Compare/Alberto.Dcb.Benchmarks.Compare.csproj" />
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~ReportRendererTests"`
Expected: FAIL — `ReportRenderer` does not exist.

- [ ] **Step 4: Implement the renderer**

Create `benchmarks/Alberto.Dcb.Benchmarks.Compare/ReportRenderer.cs`:

```csharp
using System.Globalization;
using System.Text;
using Alberto.Dcb.Benchmarks.Core;

namespace Alberto.Dcb.Benchmarks.Compare;

/// <summary>Renders a comparison as GitHub-flavoured markdown for the workflow job summary.</summary>
public static class ReportRenderer
{
    public static string ToMarkdown(ComparisonReport report)
    {
        var builder = new StringBuilder();

        builder.AppendLine("| Benchmark | Baseline | Candidate | Mean Δ | Alloc Δ | Verdict |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | --- |");

        foreach (var delta in report.Deltas)
        {
            builder.AppendLine(string.Join(" | ",
                "| " + delta.Key,
                Nanoseconds(delta.BaselineMeanNs),
                Nanoseconds(delta.CandidateMeanNs),
                Signed(delta.MeanPercent),
                Signed(delta.AllocatedPercent),
                Verdict(delta.Verdict) + " |"));
        }

        return builder.ToString();
    }

    private static string Verdict(ComparisonVerdict verdict) => verdict switch
    {
        ComparisonVerdict.Regressed => "**REGRESSED**",
        ComparisonVerdict.Improved => "improved",
        ComparisonVerdict.Added => "ADDED",
        ComparisonVerdict.Removed => "REMOVED",
        _ => "unchanged",
    };

    private static string Nanoseconds(double? value)
        => value is null ? "—" : value.Value.ToString("N1", CultureInfo.InvariantCulture) + " ns";

    private static string Signed(double? percent)
        => percent is null
            ? "—"
            : (percent.Value >= 0 ? "+" : "") + percent.Value.ToString("F1", CultureInfo.InvariantCulture) + "%";
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~ReportRendererTests"`
Expected: PASS, 6 tests.

- [ ] **Step 6: Implement the CLI entry point**

Create `benchmarks/Alberto.Dcb.Benchmarks.Compare/Program.cs`:

```csharp
using Alberto.Dcb.Benchmarks.Compare;
using Alberto.Dcb.Benchmarks.Core;

// Usage:
//   compare --import <bdn-report-full.json> --out <candidate.json> [--git-sha <sha>] [--version <v>]
//   compare --baseline <baseline.json> --candidate <candidate.json> [--markdown <out.md>]
//   compare --baseline <baseline.json> --candidate <candidate.json> --accept
//
// Exit codes: 0 = no regression, 1 = regression detected, 2 = usage or IO error.

var options = ParseArgs(args);

try
{
    if (options.TryGetValue("import", out var bdnReportPath))
    {
        var outputPath = Require(options, "out");

        // A full run spans several benchmark classes and writes one report each, so --import
        // accepts the results directory as well as a single file.
        var reportPaths = Directory.Exists(bdnReportPath)
            ? Directory.GetFiles(bdnReportPath, "*-report-full.json", SearchOption.AllDirectories)
            : [bdnReportPath];

        if (reportPaths.Length == 0)
        {
            throw new InvalidOperationException($"No *-report-full.json found under {bdnReportPath}.");
        }

        var metadata = new RunMetadata(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            GitSha: options.GetValueOrDefault("git-sha", "unknown"),
            ProfileId: MachineProfile.Capture(
                postgresImage: options.GetValueOrDefault("postgres-image", "unknown"),
                externalPostgres: options.ContainsKey("external-postgres")).ProfileId,
            AlbertoVersion: options.GetValueOrDefault("version", "0.0.0"));

        var run = BdnImporter.ImportMany(reportPaths.Select(File.ReadAllText), metadata);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, run.ToJson());

        Console.WriteLine(
            $"Imported {run.Measurements.Count} measurements from {reportPaths.Length} report(s) to {outputPath}");
        Console.WriteLine($"Machine profile: {run.Run.ProfileId}");
        return 0;
    }

    var baselinePath = Require(options, "baseline");
    var candidatePath = Require(options, "candidate");

    var candidate = BenchmarkRun.FromJson(File.ReadAllText(candidatePath));

    if (!File.Exists(baselinePath))
    {
        // First run on a new machine profile: nothing to compare against yet.
        Console.WriteLine($"No baseline at {baselinePath}. Writing the candidate as the first baseline.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(baselinePath))!);
        File.WriteAllText(baselinePath, candidate.ToJson());
        return 0;
    }

    var baseline = BenchmarkRun.FromJson(File.ReadAllText(baselinePath));
    var report = ResultComparer.Compare(baseline, candidate, Thresholds.Default);
    var markdown = ReportRenderer.ToMarkdown(report);

    Console.WriteLine(markdown);

    if (options.TryGetValue("markdown", out var markdownPath))
    {
        File.WriteAllText(markdownPath, markdown);
    }

    if (options.ContainsKey("accept"))
    {
        File.WriteAllText(baselinePath, candidate.ToJson());
        Console.WriteLine($"Baseline promoted: {baselinePath}");
        return 0;
    }

    if (report.HasRegression)
    {
        Console.Error.WriteLine("Regression detected against the committed baseline.");
        return 1;
    }

    Console.WriteLine("No regression against the committed baseline.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);

    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var name = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);

        parsed[name] = hasValue ? args[++i] : "true";
    }

    return parsed;
}

static string Require(Dictionary<string, string> options, string name)
    => options.TryGetValue(name, out var value) && value != "true"
        ? value
        : throw new InvalidOperationException($"Missing required argument --{name}.");
```

- [ ] **Step 7: Verify the CLI builds and reports usage errors**

Run: `dotnet run --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- --baseline missing.json`
Expected: prints `Missing required argument --candidate.` and exits 2.

- [ ] **Step 8: Commit**

```bash
git add benchmarks/Alberto.Dcb.Benchmarks.Compare tests/Alberto.Dcb.Tests AlbertoV3.slnx && git commit -m "feat(benchmarks): comparison CLI with markdown report and manual baseline promotion"
```

---

### Task 7: Retire the InMemory suite and repoint the benchmark project

**Files:**
- Delete: `benchmarks/Alberto.Dcb.Benchmarks/Benchmarks/AppendBenchmarks.cs`
- Delete: `benchmarks/Alberto.Dcb.Benchmarks/Benchmarks/ReadBenchmarks.cs`
- Delete: `benchmarks/Alberto.Dcb.Benchmarks/Benchmarks/CheckpointBenchmarks.cs`
- Modify: `benchmarks/Alberto.Dcb.Benchmarks/Alberto.Dcb.Benchmarks.csproj`
- Modify: `.gitignore`

- [ ] **Step 1: Delete the InMemory benchmark classes**

```bash
git rm benchmarks/Alberto.Dcb.Benchmarks/Benchmarks/AppendBenchmarks.cs benchmarks/Alberto.Dcb.Benchmarks/Benchmarks/ReadBenchmarks.cs benchmarks/Alberto.Dcb.Benchmarks/Benchmarks/CheckpointBenchmarks.cs
```

- [ ] **Step 2: Repoint the project at Postgres**

Replace the two `ItemGroup` elements in `benchmarks/Alberto.Dcb.Benchmarks/Alberto.Dcb.Benchmarks.csproj` with:

```xml
  <ItemGroup>
    <ProjectReference Include="../../src/Alberto.Dcb/Alberto.Dcb.csproj" />
    <ProjectReference Include="../../src/Alberto.Dcb.Postgres/Alberto.Dcb.Postgres.csproj" />
    <ProjectReference Include="../Alberto.Dcb.Benchmarks.Core/Alberto.Dcb.Benchmarks.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" />
    <PackageReference Include="Npgsql" />
    <PackageReference Include="Testcontainers.PostgreSql" />
  </ItemGroup>
```

The `Alberto.Dcb.InMemory` reference is gone — the suite no longer measures that backend. Leave the existing `PropertyGroup` untouched, including the comment explaining why `TreatWarningsAsErrors` is absent.

- [ ] **Step 3: Ignore BenchmarkDotNet's scratch output**

BDN writes `BenchmarkDotNet.Artifacts/` into the invocation directory, and nothing in `.gitignore` matches it. Add to `.gitignore`:

```
BenchmarkDotNet.Artifacts/
```

`benchmarks/results/` must stay tracked — do not add it.

- [ ] **Step 4: Verify the project still builds**

Run: `dotnet build benchmarks/Alberto.Dcb.Benchmarks/Alberto.Dcb.Benchmarks.csproj -c Release`
Expected: build succeeds. `Program.cs` still compiles because `BenchmarkSwitcher` finds zero benchmark classes, which is legal.

- [ ] **Step 5: Commit**

```bash
git add -A benchmarks .gitignore && git commit -m "refactor(benchmarks): retire the InMemory suite, repoint at Postgres"
```

---

### Task 8: Postgres provisioning harness

**Files:**
- Create: `benchmarks/Alberto.Dcb.Benchmarks/Harness/BenchmarkDatabase.cs`
- Create: `benchmarks/Alberto.Dcb.Benchmarks/Harness/StoreSizes.cs`

**Interfaces:**
- Consumes: `EventPlan.Build` (Task 5); `PostgresMigrator.Migrate(string connectionString, string? schema = null, bool singleTenant = false)`; `PostgresEventStoreBackend(NpgsqlDataSource dataSource, TimeProvider? timeProvider = null, string? schema = null, bool enableStableHeadBarrier = true)`.
- Produces: `BenchmarkDatabase.Instance`, `BenchmarkDatabase.CloneAsync(int storeSize, string label) → Task<string>`, `BenchmarkDatabase.PostgresImage`, `BenchmarkDatabase.IsExternal`.

**Constraints this task must honour (from the existing test-suite fixture):**
- The connection used to build a template MUST set `Pooling=false`. A pooled connection returns to the pool but leaves the physical session open, and `CREATE DATABASE … TEMPLATE` fails with *"source database is being accessed by other users"*.
- Postgres identifiers cap at 63 bytes — database names must be truncated.
- The container raises `max_connections` to 200; per-clone pools must stay small.
- `PostgresMigrator.Migrate` can both return `Successful=false` **and** throw `NpgsqlException`. Handle both.

- [ ] **Step 1: Define the store sizes**

Create `benchmarks/Alberto.Dcb.Benchmarks/Harness/StoreSizes.cs`:

```csharp
namespace Alberto.Dcb.Benchmarks.Harness;

/// <summary>
/// The seeded store sizes benchmarks run against.
///
/// Applied only where table size can plausibly change the answer. Index and over-scan
/// problems are invisible at 10k and obvious at 1M; appends that never read do not get
/// this axis, because paying for it there would triple runtime for no signal.
/// </summary>
public static class StoreSizes
{
    public const int Small = 10_000;
    public const int Medium = 100_000;
    public const int Large = 1_000_000;
}
```

- [ ] **Step 2: Implement the harness**

Create `benchmarks/Alberto.Dcb.Benchmarks/Harness/BenchmarkDatabase.cs`:

```csharp
using System.Collections.Concurrent;
using Alberto.Dcb.Benchmarks.Core;
using Alberto.Dcb.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Alberto.Dcb.Benchmarks.Harness;

/// <summary>
/// Owns the Postgres the whole benchmark process runs against.
///
/// Each store size is migrated and seeded ONCE into a template database; every benchmark
/// class then clones that template in its GlobalSetup. Cloning is a file copy inside
/// Postgres, so per-class setup costs about a second instead of re-seeding a million rows.
///
/// Mirrors tests/Alberto.Dcb.Tests/Infrastructure/PostgresCluster.cs, including its
/// load-bearing pooling constraint.
/// </summary>
public sealed class BenchmarkDatabase : IAsyncDisposable
{
    private const string Image = "postgres:16-alpine";
    private const int MaxConnections = 200;
    private const int SeedBatchSize = 1_000;

    private static readonly Lazy<Task<BenchmarkDatabase>> Lazy = new(CreateAsync);

    private readonly PostgreSqlContainer? _container;
    private readonly string _adminConnectionString;
    private readonly ConcurrentDictionary<int, Lazy<Task>> _templates = new();
    private int _databaseCount;

    private BenchmarkDatabase(PostgreSqlContainer? container, string adminConnectionString)
    {
        _container = container;
        _adminConnectionString = adminConnectionString;
    }

    /// <summary>The process-wide instance. Started on first use.</summary>
    public static Task<BenchmarkDatabase> Instance => Lazy.Value;

    /// <summary>Recorded in the machine profile so results are keyed by what they ran against.</summary>
    public string PostgresImage => IsExternal ? "external" : Image;

    public bool IsExternal => _container is null;

    private static async Task<BenchmarkDatabase> CreateAsync()
    {
        // An external Postgres lets a tuned host be measured instead of a container.
        var external = Environment.GetEnvironmentVariable("ALBERTO_BENCH_POSTGRES");
        if (!string.IsNullOrWhiteSpace(external))
        {
            return new BenchmarkDatabase(container: null, external);
        }

        var container = new PostgreSqlBuilder(Image)
            .WithCommand("-c", $"max_connections={MaxConnections}")
            .Build();

        await container.StartAsync();

        return new BenchmarkDatabase(container, container.GetConnectionString());
    }

    /// <summary>
    /// Returns a connection string to a fresh database cloned from the template for
    /// <paramref name="storeSize"/>, building and seeding that template on first use.
    /// </summary>
    public async Task<string> CloneAsync(int storeSize, string label)
    {
        await _templates.GetOrAdd(storeSize, size => new Lazy<Task>(() => BuildTemplateAsync(size))).Value;

        var database = NextDatabaseName(label);

        await using (var connection = new NpgsqlConnection(_adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""CREATE DATABASE "{database}" TEMPLATE "{TemplateName(storeSize)}" """;
            await command.ExecuteNonQueryAsync();
        }

        // Small pools: the server allows 200 connections and many classes run in one process.
        return ConnectionStringFor(database, b => b.MaxPoolSize = 10);
    }

    private async Task BuildTemplateAsync(int storeSize)
    {
        var template = TemplateName(storeSize);

        await using (var connection = new NpgsqlConnection(_adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""CREATE DATABASE "{template}" """;
            await command.ExecuteNonQueryAsync();
        }

        // Pooling MUST be off. A pooled connection is returned to the pool on close but its
        // physical session stays open, and CREATE DATABASE ... TEMPLATE refuses to run while
        // any session is attached to the source database.
        var buildConnectionString = ConnectionStringFor(template, b => b.Pooling = false);

        MigrationResult result;
        try
        {
            result = PostgresMigrator.Migrate(buildConnectionString, schema: null, singleTenant: true);
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException($"Migrating template '{template}' threw.", ex);
        }

        if (!result.Successful)
        {
            throw new InvalidOperationException($"Migrating template '{template}' failed.", result.Error);
        }

        await SeedAsync(buildConnectionString, storeSize);
    }

    private static async Task SeedAsync(string connectionString, int storeSize)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var backend = new PostgresEventStoreBackend(dataSource);

        var events = EventPlan.Build(storeSize, seed: 42);

        for (var offset = 0; offset < events.Count; offset += SeedBatchSize)
        {
            var batch = events.Skip(offset).Take(SeedBatchSize).ToArray();
            await backend.AppendAsync(batch);
        }

        // Without current statistics the planner picks different plans between runs and the
        // suite measures the planner's mood rather than the code.
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM ANALYZE";
        await command.ExecuteNonQueryAsync();
    }

    private static string TemplateName(int storeSize) => $"bench_tmpl_st_{storeSize}";

    private string ConnectionStringFor(string database, Action<NpgsqlConnectionStringBuilder>? configure = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = database };
        configure?.Invoke(builder);
        return builder.ConnectionString;
    }

    private string NextDatabaseName(string label)
    {
        var slug = new string(label.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        var suffix = $"_{Interlocked.Increment(ref _databaseCount)}";

        // Postgres caps identifiers at 63 bytes.
        var maxSlug = Math.Max(1, 63 - suffix.Length);
        if (slug.Length > maxSlug)
        {
            slug = slug[..maxSlug];
        }

        return slug + suffix;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
```

- [ ] **Step 3: Verify it builds**

Run: `dotnet build benchmarks/Alberto.Dcb.Benchmarks/Alberto.Dcb.Benchmarks.csproj -c Release`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add benchmarks/Alberto.Dcb.Benchmarks && git commit -m "feat(benchmarks): Postgres provisioning with seeded template cloning"
```

---

### Task 9: Shared BenchmarkDotNet configuration

**Files:**
- Create: `benchmarks/Alberto.Dcb.Benchmarks/Harness/BenchmarkConfig.cs`
- Modify: `benchmarks/Alberto.Dcb.Benchmarks/Program.cs`

**Interfaces:**
- Produces: `BenchmarkConfig` (a `ManualConfig` subclass) and the category constants `Categories.Append`, `Categories.Query`, `Categories.Smoke`.

- [ ] **Step 1: Implement the config**

Create `benchmarks/Alberto.Dcb.Benchmarks/Harness/BenchmarkConfig.cs`:

```csharp
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;

namespace Alberto.Dcb.Benchmarks.Harness;

/// <summary>Category names, used for --anyCategories filtering.</summary>
public static class Categories
{
    public const string Append = "append";
    public const string Query = "query";

    /// <summary>
    /// The smallest possible subset, run on every PR with --job dry. It proves the suite
    /// still compiles and executes; it measures nothing useful.
    /// </summary>
    public const string Smoke = "smoke";
}

/// <summary>
/// Shared configuration for every workload.
///
/// RunStrategy.Monitoring rather than Throughput: the work is IO-dominated, so BDN's default
/// statistical machinery mostly burns wall-clock without sharpening the result. MemoryDiagnoser
/// stays on because allocation counts remain near-deterministic even when timing is noisy,
/// which is what keeps allocation regressions detectable at all.
/// </summary>
public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithWarmupCount(2)
            .WithIterationCount(10)
            .WithInvocationCount(1)
            .WithUnrollFactor(1));

        AddDiagnoser(MemoryDiagnoser.Default);

        // JsonExporter.Full is what the compare tool imports; the others are for humans.
        AddExporter(JsonExporter.Full);
        AddExporter(MarkdownExporter.GitHub);

        AddColumnProvider(DefaultColumnProviders.Instance);
        AddLogger(ConsoleLogger.Default);
    }
}
```

- [ ] **Step 2: Restore the benchmark switcher**

Replace the body of `benchmarks/Alberto.Dcb.Benchmarks/Program.cs` with:

```csharp
using BenchmarkDotNet.Running;

// Run everything:
//   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks
//
// Run one family:
//   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --anyCategories=append
//   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --anyCategories=query
//
// Smoke run (proves it compiles and executes; measures nothing):
//   dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --job dry --anyCategories=smoke
//
// Against an existing Postgres instead of Testcontainers:
//   ALBERTO_BENCH_POSTGRES="Host=...;Database=...;Username=...;Password=..." dotnet run ...

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

// Program is referenced by FromAssembly above; the partial declaration keeps top-level
// statements and the type reference compatible.
public partial class Program;
```

- [ ] **Step 3: Verify it builds**

Run: `dotnet build benchmarks/Alberto.Dcb.Benchmarks/Alberto.Dcb.Benchmarks.csproj -c Release`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add benchmarks/Alberto.Dcb.Benchmarks && git commit -m "feat(benchmarks): shared BenchmarkDotNet configuration and category filters"
```

---

### Task 10: Append family

**Files:**
- Create: `benchmarks/Alberto.Dcb.Benchmarks/Workloads/AppendBenchmarks.cs`

**Interfaces:**
- Consumes: `BenchmarkDatabase.Instance`, `BenchmarkDatabase.CloneAsync` (Task 8); `BenchmarkConfig`, `Categories` (Task 9); `EventPlan` (Task 5).
- Produces: `AppendBenchmarkBase`, `AppendBenchmarks`, `BatchAppendBenchmarks`, `TagFanOutBenchmarks`.

**The reset constraint:** these benchmarks write. Without cleanup, iteration 50 appends into a table 50 iterations larger than iteration 1's, and the run measures growth rather than the append path. `[IterationCleanup]` deletes back to the seeded head; the tag- and type-position tables cascade.

**Why three classes:** `[Params]` applies to every method in its class, so a single class carrying both `BatchSize` and `TagsPerEvent` would run all five append cases nine times over — most of them identical work under different result keys.

- [ ] **Step 1: Implement the append workloads**

Create `benchmarks/Alberto.Dcb.Benchmarks/Workloads/AppendBenchmarks.cs`:

```csharp
using Alberto.Dcb.Benchmarks.Core;
using Alberto.Dcb.Benchmarks.Harness;
using Alberto.Dcb.Postgres;
using BenchmarkDotNet.Attributes;
using Npgsql;

namespace Alberto.Dcb.Benchmarks.Workloads;

/// <summary>
/// Shared setup for the append workloads: a cloned store, a backend, and cleanup back to the
/// seeded head between iterations.
///
/// StoreSize is deliberately absent throughout: appending does not read, so table size does
/// not change the answer.
/// </summary>
public abstract class AppendBenchmarkBase
{
    protected NpgsqlDataSource DataSource = null!;
    protected PostgresEventStoreBackend Backend = null!;
    protected long SeededHead;

    [GlobalSetup]
    public async Task Setup()
    {
        var database = await BenchmarkDatabase.Instance;
        var connectionString = await database.CloneAsync(StoreSizes.Medium, GetType().Name);

        DataSource = NpgsqlDataSource.Create(connectionString);
        Backend = new PostgresEventStoreBackend(DataSource);
        SeededHead = await Backend.GetLastPositionAsync();

        await OnSetupAsync();
    }

    protected virtual Task OnSetupAsync() => Task.CompletedTask;

    /// <summary>
    /// Deletes everything this iteration appended, so each iteration starts against an
    /// identically sized table. Without it, iteration 50 measures a bigger store than
    /// iteration 1 and the run reports growth rather than the append path.
    /// </summary>
    [IterationCleanup]
    public void ResetToSeededHead()
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        // The tag and type position tables cascade from alberto_events.
        command.CommandText = "DELETE FROM alberto_events WHERE global_position > @head";
        command.Parameters.AddWithValue("head", SeededHead);
        command.ExecuteNonQuery();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await DataSource.DisposeAsync();
}

/// <summary>Append cases with no parameter axis of their own.</summary>
[Config(typeof(BenchmarkConfig))]
public class AppendBenchmarks : AppendBenchmarkBase
{
    private IEventToPersist[] _single = null!;
    private DcbQuery _neverConflictingQuery = null!;
    private DcbQuery _alwaysConflictingQuery = null!;

    protected override Task OnSetupAsync()
    {
        // Pre-built so the measurement is the append path, not object construction.
        _single = [.. EventPlan.Build(1, seed: 7)];

        // Targets a tag never written, so the check always takes the no-conflict path
        // without throwing — the happy path worth measuring.
        _neverConflictingQuery = DcbQuery.ByTags(EventTag.FromStorage("never", "used"));

        // Targets a tag the seed definitely wrote, so the check always finds a conflict.
        _alwaysConflictingQuery = DcbQuery.ByTags(EventTag.FromStorage("order", "1"));

        return Task.CompletedTask;
    }

    [Benchmark(Baseline = true), BenchmarkCategory(Categories.Append, Categories.Smoke)]
    public Task<IReadOnlyCollection<IEventEnvelope>> SingleAppend()
        => Backend.AppendAsync(_single);

    [Benchmark, BenchmarkCategory(Categories.Append)]
    public Task<IReadOnlyCollection<IEventEnvelope>> AppendWithDcbCheck()
        => Backend.AppendAsync(_single, dcbQuery: _neverConflictingQuery, expectedPosition: SeededHead);

    /// <summary>Prices the failure path — a conflict detected and thrown.</summary>
    [Benchmark, BenchmarkCategory(Categories.Append)]
    public async Task<bool> AppendWithConflictDetected()
    {
        try
        {
            await Backend.AppendAsync(_single, dcbQuery: _alwaysConflictingQuery, expectedPosition: 0);
            return false;
        }
        catch (DcbConflictException)
        {
            return true;
        }
    }
}

/// <summary>
/// Batch size sweep. Its own class because [Params] applies to every method in a class —
/// leaving BatchSize on AppendBenchmarks would run SingleAppend three times for nothing.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class BatchAppendBenchmarks : AppendBenchmarkBase
{
    private IEventToPersist[] _batch = null!;

    [Params(10, 100, 1000)]
    public int BatchSize { get; set; }

    protected override Task OnSetupAsync()
    {
        _batch = [.. EventPlan.Build(BatchSize, seed: 7)];
        return Task.CompletedTask;
    }

    [Benchmark, BenchmarkCategory(Categories.Append)]
    public Task<IReadOnlyCollection<IEventEnvelope>> BatchAppend()
        => Backend.AppendAsync(_batch);
}

/// <summary>
/// Tag fan-out sweep — one event carrying many tags, which drives the tag-position table
/// write amplification. Separate class for the same [Params] reason as above.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class TagFanOutBenchmarks : AppendBenchmarkBase
{
    private IEventToPersist[] _fanOut = null!;

    [Params(1, 5, 20)]
    public int TagsPerEvent { get; set; }

    protected override Task OnSetupAsync()
    {
        _fanOut =
        [
            new EventToPersist
            {
                EventType = new EventType("order-placed"),
                Tags = [.. Enumerable.Range(1, TagsPerEvent)
                    .Select(i => EventTag.FromStorage("order", $"fanout{i}"))],
                EventData = """{"orderId":"fanout","amount":9.99}""",
            },
        ];

        return Task.CompletedTask;
    }

    [Benchmark, BenchmarkCategory(Categories.Append)]
    public Task<IReadOnlyCollection<IEventEnvelope>> AppendWithTagFanOut()
        => Backend.AppendAsync(_fanOut);
}
```

- [ ] **Step 2: Run the smoke subset to prove it executes**

Run: `dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --job dry --anyCategories=smoke`
Expected: Docker starts a `postgres:16-alpine` container, the 100k template is migrated and seeded, `SingleAppend` runs once, and BDN reports without error.

- [ ] **Step 3: Commit**

```bash
git add benchmarks/Alberto.Dcb.Benchmarks && git commit -m "feat(benchmarks): Postgres append family"
```

---

### Task 11: Query family

**Files:**
- Create: `benchmarks/Alberto.Dcb.Benchmarks/Workloads/QueryBenchmarks.cs`

**Interfaces:**
- Consumes: `BenchmarkDatabase`, `StoreSizes` (Task 8); `BenchmarkConfig`, `Categories` (Task 9).
- Produces: `QueryBenchmarkBase`, `QueryBenchmarks`, `MultiTagQueryBenchmarks`.

Reads do not mutate, so unlike the append family no `[IterationCleanup]` is needed here.
`UnionTags` lives on its own class because `[Params]` applies to every method in a class —
leaving it on `QueryBenchmarks` would double all nine query cases for one case's benefit.

- [ ] **Step 1: Implement the query workloads**

Create `benchmarks/Alberto.Dcb.Benchmarks/Workloads/QueryBenchmarks.cs`:

```csharp
using Alberto.Dcb.Benchmarks.Harness;
using Alberto.Dcb.Postgres;
using BenchmarkDotNet.Attributes;
using Npgsql;

namespace Alberto.Dcb.Benchmarks.Workloads;

/// <summary>
/// Read throughput against PostgreSQL, at three store sizes.
///
/// Two cases here are the ones the retired InMemory suite never had. TailRead models the
/// polling steady state — reading just behind the head takes a different plan than reading
/// from position 0, and it is the read Alberto performs most often in production.
/// BoundaryRead models the small selective query before a decision, which is the latency a
/// user actually feels.
/// </summary>
public abstract class QueryBenchmarkBase
{
    protected const int PageSize = 500;

    protected NpgsqlDataSource DataSource = null!;
    protected PostgresEventStoreBackend Backend = null!;
    protected long Head;

    [Params(StoreSizes.Small, StoreSizes.Medium, StoreSizes.Large)]
    public int StoreSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var database = await BenchmarkDatabase.Instance;
        var connectionString = await database.CloneAsync(StoreSize, GetType().Name);

        DataSource = NpgsqlDataSource.Create(connectionString);
        Backend = new PostgresEventStoreBackend(DataSource);
        Head = await Backend.GetLastPositionAsync();

        await OnSetupAsync();
    }

    protected virtual Task OnSetupAsync() => Task.CompletedTask;

    // Reads do not mutate, so unlike the append family these need no iteration cleanup.
    [GlobalCleanup]
    public async Task Cleanup() => await DataSource.DisposeAsync();
}

/// <summary>Read throughput against PostgreSQL, at three store sizes.</summary>
[Config(typeof(BenchmarkConfig))]
public class QueryBenchmarks : QueryBenchmarkBase
{
    private DcbQuery _byType = null!;
    private DcbQuery _byTag = null!;
    private DcbQuery _byTypeAndTag = null!;
    private DcbQuery _boundary = null!;

    protected override Task OnSetupAsync()
    {
        // Built once: DcbQuery construction is not what these cases measure.
        _byType = DcbQuery.ByTypes("order-placed");
        _byTag = DcbQuery.ByTags(EventTag.FromStorage("order", "42"));
        _byTypeAndTag = DcbQuery.For("order", "42").WithTypes("order-placed");
        _boundary = DcbQuery.For("order", "7");

        return Task.CompletedTask;
    }

    /// <summary>Full catch-up from the beginning of the log.</summary>
    [Benchmark(Baseline = true), BenchmarkCategory(Categories.Query, Categories.Smoke)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllFromZero()
        => Backend.StreamAllAsync(afterPosition: 0, limit: PageSize);

    /// <summary>
    /// The polling steady state — reading the page just behind the head. Alberto's most
    /// frequent read in production, and a different query plan than reading from zero.
    /// The retired InMemory suite had no equivalent.
    /// </summary>
    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> TailRead()
        => Backend.StreamAllAsync(afterPosition: Math.Max(0, Head - PageSize), limit: PageSize);

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByType()
        => Backend.StreamAsync(_byType, afterPosition: 0, limit: PageSize);

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByTag()
        => Backend.StreamAsync(_byTag, afterPosition: 0, limit: PageSize);

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByTypeAndTag()
        => Backend.StreamAsync(_byTypeAndTag, afterPosition: 0, limit: PageSize);

    /// <summary>
    /// A small selective read before a decision — the latency a user actually feels.
    /// Also absent from the retired suite.
    /// </summary>
    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> BoundaryRead()
        => Backend.StreamAsync(_boundary, afterPosition: 0, limit: 50);

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<long> GetLastPosition()
        => Backend.GetLastPositionAsync();

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<long> GetStableHead()
        => Backend.GetStableHeadAsync(afterPosition: Math.Max(0, Head - PageSize));
}

/// <summary>
/// Tag-union reads — the DISTINCT-before-LIMIT over-scan path. Its own class so the
/// UnionTags axis does not multiply every other query case for no signal.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class MultiTagQueryBenchmarks : QueryBenchmarkBase
{
    private DcbQuery _byMultiTag = null!;

    [Params(2, 8)]
    public int UnionTags { get; set; }

    protected override Task OnSetupAsync()
    {
        _byMultiTag = DcbQuery
            .ByTags([.. Enumerable.Range(1, UnionTags)
                .Select(i => EventTag.FromStorage("order", i.ToString()))])
            .AsUnion();

        return Task.CompletedTask;
    }

    [Benchmark, BenchmarkCategory(Categories.Query)]
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamByMultiTag()
        => Backend.StreamAsync(_byMultiTag, afterPosition: 0, limit: PageSize);
}
```

- [ ] **Step 2: Run the query family at the smallest size to verify it executes**

Run: `dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --job dry --anyCategories=query`
Expected: all query cases execute across the three store sizes without error. Seeding 1M rows makes this run take several minutes on first execution.

- [ ] **Step 3: Regenerate the importer fixture from the real report**

The fixture in `BdnImporterTests` was hand-written against BenchmarkDotNet 0.14.0's documented shape. Now that a real report exists, verify it:

```bash
cat BenchmarkDotNet.Artifacts/results/*-report-full.json | head -60
```

If the real structure differs from the fixture — different property names, nesting, or casing — update both `BdnImporterTests.ReportJson` and `BdnImporter` to match, then re-run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~BdnImporterTests"
```

Expected: PASS. Do not skip this step — every downstream result depends on the importer reading the real format.

- [ ] **Step 4: Commit**

```bash
git add benchmarks tests && git commit -m "feat(benchmarks): Postgres query family with tail and boundary reads"
```

---

### Task 12: PR smoke run

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Add the smoke step**

In `.github/workflows/ci.yml`, add after the existing `Test` step and before `Upload coverage`:

```yaml
      # Proves the benchmark suite still compiles and executes. --job dry runs each case
      # once with no warmup, so it measures nothing — it exists to stop the suite from
      # bit-rotting between nightly runs, which is how benchmark suites usually die.
      - name: Benchmark smoke run
        run: dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --job dry --anyCategories=smoke
```

Docker is already available on `ubuntu-latest`, which is what the Testcontainers-backed tests in the step above rely on.

- [ ] **Step 2: Verify locally**

Run: `dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --job dry --anyCategories=smoke`
Expected: exits 0 in well under a minute after the container is warm.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml && git commit -m "ci: smoke-run the benchmark suite on every PR"
```

---

### Task 13: Nightly workflow and first baseline

**Files:**
- Create: `.github/workflows/benchmarks.yml`
- Create: `benchmarks/results/.gitkeep`
- Create: `benchmarks/README.md`

- [ ] **Step 1: Create the nightly workflow**

Create `.github/workflows/benchmarks.yml`:

```yaml
name: Benchmarks

on:
  schedule:
    # 02:00 UTC daily. PR CI stays fast; this is where the real suite runs.
    - cron: '0 2 * * *'
  workflow_dispatch:
    inputs:
      categories:
        description: 'Category filter (append, query, or leave blank for all)'
        required: false
        default: ''

permissions:
  contents: write

env:
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true
  # Names the profile directory. The hash still distinguishes the actual hardware.
  ALBERTO_BENCH_PROFILE_LABEL: ci

jobs:
  benchmark:
    runs-on: ubuntu-latest
    # Seeding 1M events plus ~80 cases; the default 6h cap is far too generous to rely on.
    timeout-minutes: 120

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Run benchmarks
        run: |
          FILTER=""
          if [ -n "${{ github.event.inputs.categories }}" ]; then
            FILTER="--anyCategories=${{ github.event.inputs.categories }}"
          fi
          dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- $FILTER

      # Passes the whole results directory: the run spans several benchmark classes and
      # BenchmarkDotNet writes one report per class.
      - name: Normalize results
        run: |
          dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- \
            --import BenchmarkDotNet.Artifacts/results \
            --out candidate.json \
            --git-sha "${{ github.sha }}" \
            --postgres-image "postgres:16-alpine"

      - name: Resolve profile id
        id: profile
        run: |
          PROFILE=$(python3 -c "import json;print(json.load(open('candidate.json'))['run']['profileId'])")
          echo "id=$PROFILE" >> "$GITHUB_OUTPUT"

      - name: Compare against baseline
        id: compare
        continue-on-error: true
        run: |
          dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- \
            --baseline "benchmarks/results/${{ steps.profile.outputs.id }}/baseline.json" \
            --candidate candidate.json \
            --markdown delta.md

      - name: Publish delta to the job summary
        if: always()
        run: cat delta.md >> "$GITHUB_STEP_SUMMARY"

      - name: Upload raw reports
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: benchmark-reports
          path: |
            BenchmarkDotNet.Artifacts/results/
            candidate.json

      # History accumulates automatically; baseline.json is only ever moved by a human
      # running `--accept`. An auto-promoting baseline ratchets silently and stops being a gate.
      - name: Append to history
        if: github.event_name == 'schedule'
        run: |
          DIR="benchmarks/results/${{ steps.profile.outputs.id }}/history"
          mkdir -p "$DIR"
          cp candidate.json "$DIR/$(date -u +%Y-%m-%dT%H-%MZ)-${GITHUB_SHA::7}.json"
          git config user.name "github-actions[bot]"
          git config user.email "github-actions[bot]@users.noreply.github.com"
          git add "$DIR"
          git diff --staged --quiet || git commit -m "chore(benchmarks): nightly result [skip ci]"
          git push

      - name: Fail if a regression was detected
        if: steps.compare.outcome == 'failure'
        run: |
          echo "Benchmark regression detected. See the job summary." >&2
          exit 1
```

- [ ] **Step 2: Create the results directory and document the workflow**

```bash
mkdir -p benchmarks/results && touch benchmarks/results/.gitkeep
```

Create `benchmarks/README.md`:

```markdown
# Benchmarks

Postgres-backed BenchmarkDotNet suite. Design:
[docs/superpowers/specs/2026-07-26-benchmark-suite-design.md](../docs/superpowers/specs/2026-07-26-benchmark-suite-design.md)

## Running

Everything (needs Docker; takes 30–60 minutes cold):

    dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks

One family:

    dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --anyCategories=append

Against an existing Postgres rather than Testcontainers:

    ALBERTO_BENCH_POSTGRES="Host=localhost;Database=bench;Username=postgres;Password=postgres" \
      dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks

## Comparing

Normalize a BenchmarkDotNet report, then diff it against the committed baseline:

    dotnet run --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- \
      --import BenchmarkDotNet.Artifacts/results/<report>-report-full.json --out candidate.json

    dotnet run --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- \
      --baseline benchmarks/results/<profileId>/baseline.json --candidate candidate.json

Exit code 1 means a regression. Thresholds: mean +20% (and outside the combined standard
deviation band), allocations +10% (no noise band — allocation counts do not drift).

## Baselines

Results are keyed by machine profile. Comparing across profiles is refused, not warned about,
so your laptop's numbers never silently diff against CI's.

Promotion is manual and deliberate:

    dotnet run --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- \
      --baseline benchmarks/results/<profileId>/baseline.json --candidate candidate.json --accept

CI appends to `history/` on every nightly run but never touches `baseline.json`.
```

- [ ] **Step 3: Produce and commit the first local baseline**

```bash
dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --anyCategories=append
```

Then normalize and store it (substitute the real report filename and the profile id the tool prints):

```bash
dotnet run --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- --import BenchmarkDotNet.Artifacts/results --out candidate.json
```

The import step prints `Machine profile: <profileId>`. Create that directory and seed the baseline:

```bash
mkdir -p benchmarks/results/<profileId> && cp candidate.json benchmarks/results/<profileId>/baseline.json
```

- [ ] **Step 4: Verify the comparer reports no regression against itself**

Run: `dotnet run --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- --baseline benchmarks/results/<profileId>/baseline.json --candidate candidate.json`
Expected: a delta table where every row reads `unchanged`, and exit code 0.

- [ ] **Step 5: Commit**

```bash
git add benchmarks .github/workflows/benchmarks.yml && git commit -m "ci: nightly benchmark workflow and first committed baseline"
```

---

## Spec deviations

Three refinements to the spec, to be folded back into it:

1. **Four projects, not three.** `Alberto.Dcb.Benchmarks.Core` was extracted so the schema, comparer, and event-plan logic can be unit-tested without pulling BenchmarkDotNet into the test project's build.
2. **`InternalsVisibleTo` lives in `src/Alberto.Dcb/Alberto.Dcb.csproj`,** as MSBuild `ItemGroup/InternalsVisibleTo` items — not in `AssemblyInfo.cs`, which contains only a comment redirecting there. The spec says otherwise. This affects Phase 3, not this plan.
3. **Profile ids are `<label>-<hash8>`** (e.g. `ci-3f2a91b8`), not the spec's illustrative `ci-ubuntu-x64`. A hash over every hardware field cannot silently collide when the runner image changes.

## Out of scope

Phases 3–5 get their own plans: Checkpoint and State-store families, the macro throughput
harness, Outbox, Tenancy, Sharding, and the Marten parity comparison.
