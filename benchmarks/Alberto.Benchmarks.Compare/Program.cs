using Alberto.Benchmarks.Compare;
using Alberto.Benchmarks.Core;

// Usage:
//   compare --import <bdn-report-full.json|results-dir> --out <candidate.json>
//           --postgres-image <tag> | --external-postgres  [--git-sha <sha>] [--version <v>]
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

        // The image is part of the profile hash, so defaulting it would mint a profile no
        // real baseline can ever match -- and the comparer refuses mismatched profiles
        // rather than warning, so the mistake surfaces much later as "no baseline found".
        // Demand it instead. --external-postgres already pins the field to "external".
        var externalPostgres = options.ContainsKey("external-postgres");
        if (!externalPostgres && !options.ContainsKey("postgres-image"))
        {
            throw new InvalidOperationException(
                "--postgres-image is required (or pass --external-postgres). It is part of the "
                + "machine profile, so importing without it produces results that cannot be "
                + "compared against any baseline.");
        }

        var metadata = new RunMetadata(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            GitSha: options.GetValueOrDefault("git-sha", "unknown"),
            ProfileId: MachineProfile.Capture(
                postgresImage: options.GetValueOrDefault("postgres-image", "unknown"),
                externalPostgres: externalPostgres).ProfileId,
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
