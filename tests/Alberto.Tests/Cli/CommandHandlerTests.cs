using Alberto.Admin;
using Alberto.Cli;
using Alberto.Cli.Commands;
using Alberto.Cli.Output;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Cli;

/// <summary>
/// Tests for the extracted Render methods on the read-only CLI commands. These exercise the
/// json-vs-table branch and exit code with a fake IOutput and pre-fabricated ShardResult data,
/// so no database or CliSession is involved.
/// </summary>
public sealed class CommandHandlerTests
{
    // ── CheckpointsCommand ───────────────────────────────────────────────────────

    [Fact]
    public void Checkpoints_JsonMode_EmitsJson_NotTable()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<CheckpointInfo>)
        [
            new CheckpointInfo("my-processor", 42, null)
        ]);

        var code = CheckpointsCommand.Render(output, [target], results, json: true);

        code.Should().Be(0);
        output.JsonCalls.Should().HaveCount(1);
        output.TableCalls.Should().BeEmpty();
    }

    [Fact]
    public void Checkpoints_HumanMode_EmitsTable_NotJson()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<CheckpointInfo>)
        [
            new CheckpointInfo("my-processor", 42, null)
        ]);

        var code = CheckpointsCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().HaveCount(1);
        output.JsonCalls.Should().BeEmpty();
    }

    [Fact]
    public void Checkpoints_EmptyResults_HumanMode_EmitsNoRowsText()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<CheckpointInfo>)[]);

        var code = CheckpointsCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().BeEmpty();
        output.TextCalls.Should().ContainSingle(t => t.Contains("No checkpoints found"));
    }

    [Fact]
    public void Checkpoints_ShardFailure_ReturnsExitCode1_AndEmitsError()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Failure<IReadOnlyList<CheckpointInfo>>(target, "connection refused");

        var code = CheckpointsCommand.Render(output, [target], results, json: false);

        code.Should().Be(1);
        output.ErrorCalls.Should().ContainSingle(e => e.Contains("connection refused"));
    }

    // ── DeadLettersCommand ───────────────────────────────────────────────────────

    [Fact]
    public void DeadLetters_JsonMode_EmitsJson_NotTable()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<DeadLetterInfo>)
        [
            new DeadLetterInfo(Guid.NewGuid(), "processor-a", "OrderPlaced", 100, "boom", null, null)
        ]);

        var code = DeadLettersCommand.Render(output, [target], results, json: true);

        code.Should().Be(0);
        output.JsonCalls.Should().HaveCount(1);
        output.TableCalls.Should().BeEmpty();
    }

    [Fact]
    public void DeadLetters_HumanMode_EmitsTable_NotJson()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<DeadLetterInfo>)
        [
            new DeadLetterInfo(Guid.NewGuid(), "processor-a", "OrderPlaced", 100, "boom", null, null)
        ]);

        var code = DeadLettersCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().HaveCount(1);
        output.JsonCalls.Should().BeEmpty();
    }

    [Fact]
    public void DeadLetters_ShardFailure_ReturnsExitCode1()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Failure<IReadOnlyList<DeadLetterInfo>>(target, "timeout");

        var code = DeadLettersCommand.Render(output, [target], results, json: true);

        code.Should().Be(1);
        output.ErrorCalls.Should().ContainSingle(e => e.Contains("timeout"));
    }

    // ── ProcessorCommand ─────────────────────────────────────────────────────────

    [Fact]
    public void Processor_JsonMode_EmitsJson_NotBox()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<CheckpointInfo>)
        [
            new CheckpointInfo("my-processor", 77, null)
        ]);

        var code = ProcessorCommand.Render("my-processor", output, [target], results, json: true);

        code.Should().Be(0);
        output.JsonCalls.Should().HaveCount(1);
        output.BoxCalls.Should().BeEmpty();
    }

    [Fact]
    public void Processor_HumanMode_EmitsBox_NotJson()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<CheckpointInfo>)
        [
            new CheckpointInfo("my-processor", 77, null)
        ]);

        var code = ProcessorCommand.Render("my-processor", output, [target], results, json: false);

        code.Should().Be(0);
        output.BoxCalls.Should().HaveCount(1);
        output.JsonCalls.Should().BeEmpty();
    }

    [Fact]
    public void Processor_NotFound_HumanMode_ReturnsExitCode1_AndEmitsWarning()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");

        // An empty result means the processor was not found.
        var results = Success(target, (IReadOnlyList<CheckpointInfo>)[]);

        var code = ProcessorCommand.Render("missing-proc", output, [target], results, json: false);

        code.Should().Be(1);
        output.WarningCalls.Should().ContainSingle(w => w.Contains("missing-proc"));
        output.BoxCalls.Should().BeEmpty();
    }

    [Fact]
    public void Processor_NotFound_JsonMode_EmitsEmptyJson_AndReturnsExitCode1()
    {
        // In json mode the not-found branch still emits json (an empty flatten), not a warning.
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<CheckpointInfo>)[]);

        var code = ProcessorCommand.Render("missing-proc", output, [target], results, json: true);

        code.Should().Be(1);
        output.JsonCalls.Should().HaveCount(1);
        output.WarningCalls.Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ShardResult<T>> Success<T>(ShardTarget target, T value) =>
        [new ShardResult<T>(target, value, null)];

    private static IReadOnlyList<ShardResult<T>> Failure<T>(ShardTarget target, string message) =>
        [new ShardResult<T>(target, default, new InvalidOperationException(message))];
}

/// <summary>
/// Test double that records every IOutput call so tests can assert on what was written.
/// HumanOutput routes Text/Table/Box through Spectre's internal console (which ignores
/// Console.SetOut), so a real output adapter cannot be observed in tests — hence this double.
/// </summary>
internal sealed class TestOutput : IOutput
{
    public List<object> JsonCalls { get; } = [];
    public List<string> TextCalls { get; } = [];
    public List<string> ErrorCalls { get; } = [];
    public List<string> WarningCalls { get; } = [];
    public List<(string[] Headers, string[][] Rows)> TableCalls { get; } = [];
    public List<(string Title, Dictionary<string, string> Fields)> BoxCalls { get; } = [];

    public void Text(string text) => TextCalls.Add(text);

    public void Table(string[] headers, IEnumerable<string[]> rows) =>
        TableCalls.Add((headers, rows.ToArray()));

    public void Box(string title, Dictionary<string, string> fields) =>
        BoxCalls.Add((title, fields));

    public void Json(object data) => JsonCalls.Add(data);
    public void Warning(string message) => WarningCalls.Add(message);
    public void Error(string message) => ErrorCalls.Add(message);
}
