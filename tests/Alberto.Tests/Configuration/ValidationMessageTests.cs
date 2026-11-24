using Alberto.Configuration;
using Xunit;
using Alberto.Results;

namespace Alberto.Tests.Configuration;

public class ValidationMessageTests
{
    [Fact]
    public void A_failure_renders_its_code_problem_and_remedy()
    {
        var failure = new AlbertoValidationFailure(
            "ALB0001",
            "No event store backend is configured.",
            "Add .WithPostgres(...) or .WithInMemory() inside AddAlberto(\"orders\", ...).");

        Assert.Equal("[ALB0001] No event store backend is configured." + Environment.NewLine +
            "          → Add .WithPostgres(...) or .WithInMemory() inside AddAlberto(\"orders\", ...).", failure.Format());
    }

    [Fact]
    public void The_report_names_the_module_and_counts_the_problems()
    {
        var report = AlbertoValidationReport.Describe("orders",
        [
            new AlbertoValidationFailure("ALB0001", "First problem.", "First remedy."),
            new AlbertoValidationFailure("ALB0002", "Second problem.", "Second remedy."),
        ]);

        Assert.StartsWith("Alberto module 'orders' cannot start: 2 configuration problems.", report);
        Assert.Contains("[ALB0001] First problem.", report);
        Assert.Contains("[ALB0002] Second problem.", report);
        Assert.Contains("→ Second remedy.", report);
    }

    [Fact]
    public void One_problem_is_reported_in_the_singular()
    {
        var report = AlbertoValidationReport.Describe("orders",
            [new AlbertoValidationFailure("ALB0001", "Only problem.", "Only remedy.")]);

        Assert.StartsWith("Alberto module 'orders' cannot start: 1 configuration problem.", report);
    }

    [Fact]
    public void The_report_ends_with_the_configuration_path_hint()
    {
        var report = AlbertoValidationReport.Describe("orders",
            [new AlbertoValidationFailure("ALB0001", "Problem.", "Remedy.")]);

        Assert.EndsWith("Settings can also be supplied under 'Alberto:Modules:orders' in configuration.", report);
    }
}
