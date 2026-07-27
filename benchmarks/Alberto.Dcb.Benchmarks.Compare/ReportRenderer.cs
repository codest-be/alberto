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
