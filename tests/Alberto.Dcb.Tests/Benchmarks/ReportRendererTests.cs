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
