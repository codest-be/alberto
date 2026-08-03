using Alberto.Admin;
using Alberto.Cli;
using Alberto.Cli.Commands;
using Alberto.Cli.Output;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Cli;

/// <summary>
/// Tests for the extracted Render methods on the read-only CLI commands
/// (Checkpoints, DeadLetters, Events, Processor, Projections, Status, System, Tenants).
/// These exercise the json-vs-table branch and exit code with a fake IOutput and
/// pre-fabricated ShardResult data, so no database or CliSession is involved.
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

    // ── EventsCommand ────────────────────────────────────────────────────────────

    [Fact]
    public void Events_JsonMode_EmitsJson_NotTable()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<EventInfo>)
        [
            new EventInfo(1, "OrderPlaced", "order:42", null, null)
        ]);

        var code = EventsCommand.Render(output, [target], results, json: true);

        code.Should().Be(0);
        output.JsonCalls.Should().HaveCount(1);
        output.TableCalls.Should().BeEmpty();
    }

    [Fact]
    public void Events_HumanMode_EmitsTable_NotJson()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<EventInfo>)
        [
            new EventInfo(1, "OrderPlaced", "order:42", null, null)
        ]);

        var code = EventsCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().HaveCount(1);
        output.JsonCalls.Should().BeEmpty();
    }

    [Fact]
    public void Events_HumanMode_RowProjection_HasExpectedColumns()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<EventInfo>)
        [
            new EventInfo(99, "PaymentReceived", "pay:7", null, "tenant-a")
        ]);

        EventsCommand.Render(output, [target], results, json: false);

        var (headers, rows) = output.TableCalls.Should().ContainSingle().Subject;
        headers.Should().Equal("Position", "Event Type", "Tags", "Tenant ID", "Created At");
        rows.Should().HaveCount(1);
        rows[0].Should().Equal("99", "PaymentReceived", "pay:7", "tenant-a", "-");
    }

    [Fact]
    public void Events_EmptyResults_HumanMode_EmitsNoEventsText()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<EventInfo>)[]);

        var code = EventsCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().BeEmpty();
        output.TextCalls.Should().ContainSingle(t => t.Contains("No events found"));
    }

    [Fact]
    public void Events_ShardFailure_ReturnsExitCode1_AndEmitsError()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Failure<IReadOnlyList<EventInfo>>(target, "connection refused");

        var code = EventsCommand.Render(output, [target], results, json: false);

        code.Should().Be(1);
        output.ErrorCalls.Should().ContainSingle(e => e.Contains("connection refused"));
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

    // ── ProjectionsCommand ───────────────────────────────────────────────────────

    [Fact]
    public void ProjectionTypes_JsonMode_EmitsJson_NotTable()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<string>)["OrderSummary", "PaymentRecord"]);

        var code = ProjectionsCommand.RenderTypes(output, results, json: true);

        code.Should().Be(0);
        output.JsonCalls.Should().HaveCount(1);
        output.TableCalls.Should().BeEmpty();
    }

    [Fact]
    public void ProjectionTypes_HumanMode_EmitsTable_NotJson()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<string>)["OrderSummary"]);

        var code = ProjectionsCommand.RenderTypes(output, results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().HaveCount(1);
        output.JsonCalls.Should().BeEmpty();
    }

    [Fact]
    public void ProjectionTypes_Empty_HumanMode_EmitsNoTypesText()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<string>)[]);

        var code = ProjectionsCommand.RenderTypes(output, results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().BeEmpty();
        output.TextCalls.Should().ContainSingle(t => t.Contains("No projection types found"));
    }

    [Fact]
    public void ProjectionTypes_DeduplicatesAcrossShards()
    {
        // Two shards each report "OrderSummary" plus one unique type.
        // RenderTypes must deduplicate into one sorted list.
        var t1 = new ShardTarget("db1", "Host=db1", "public");
        var t2 = new ShardTarget("db2", "Host=db2", "public");
        var results = new[]
        {
            new ShardResult<IReadOnlyList<string>>(t1, ["OrderSummary", "PaymentRecord"], null),
            new ShardResult<IReadOnlyList<string>>(t2, ["OrderSummary", "RefundRecord"], null),
        };

        var output = new TestOutput();
        ProjectionsCommand.RenderTypes(output, results, json: false);

        var (_, rows) = output.TableCalls.Should().ContainSingle().Subject;
        rows.Select(r => r[0]).Should().Equal("OrderSummary", "PaymentRecord", "RefundRecord");
    }

    [Fact]
    public void ProjectionList_JsonMode_EmitsJson_NotTable()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<ProjectionState>)
        [
            new ProjectionState("doc-1", null, null)
        ]);

        var code = ProjectionsCommand.RenderList("OrderSummary", output, [target], results, json: true);

        code.Should().Be(0);
        output.JsonCalls.Should().HaveCount(1);
        output.TableCalls.Should().BeEmpty();
    }

    [Fact]
    public void ProjectionList_HumanMode_EmitsTable_NotJson()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<ProjectionState>)
        [
            new ProjectionState("doc-1", null, null)
        ]);

        var code = ProjectionsCommand.RenderList("OrderSummary", output, [target], results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().HaveCount(1);
        output.JsonCalls.Should().BeEmpty();
    }

    [Fact]
    public void ProjectionList_Empty_HumanMode_EmitsNoStatesText()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, (IReadOnlyList<ProjectionState>)[]);

        var code = ProjectionsCommand.RenderList("OrderSummary", output, [target], results, json: false);

        code.Should().Be(0);
        output.TextCalls.Should().ContainSingle(t => t.Contains("No projection states found"));
    }

    // ── StatusCommand ────────────────────────────────────────────────────────────

    [Fact]
    public void Status_JsonMode_EmitsJson_NotBox()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, new StatusCommand.Status(1000, [], 0));

        var code = StatusCommand.Render(output, [target], results, json: true);

        code.Should().Be(0);
        output.JsonCalls.Should().HaveCount(1);
        output.BoxCalls.Should().BeEmpty();
    }

    [Fact]
    public void Status_HumanMode_WithProcessors_EmitsBoxAndTable()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var processors = (IReadOnlyList<ProcessorInfo>)[new ProcessorInfo("proc-1", 100, null)];
        var results = Success(target, new StatusCommand.Status(1000, processors, 0));

        var code = StatusCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.BoxCalls.Should().HaveCount(1);
        output.TableCalls.Should().HaveCount(1);
    }

    [Fact]
    public void Status_HumanMode_NoProcessors_EmitsBoxAndText()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, new StatusCommand.Status(null, [], 0));

        var code = StatusCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.BoxCalls.Should().HaveCount(1);
        output.TableCalls.Should().BeEmpty();
        output.TextCalls.Should().ContainSingle(t => t.Contains("No processors found"));
    }

    [Fact]
    public void Status_ShardFailure_ReturnsExitCode1()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Failure<StatusCommand.Status>(target, "timeout");

        var code = StatusCommand.Render(output, [target], results, json: false);

        code.Should().Be(1);
        output.ErrorCalls.Should().ContainSingle(e => e.Contains("timeout"));
    }

    // ── SystemCommand ────────────────────────────────────────────────────────────

    [Fact]
    public void System_JsonMode_EmitsJson_NotBox()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, new SystemInfo(500, 3, 0, null));

        var code = SystemCommand.Render(output, [target], results, json: true);

        code.Should().Be(0);
        output.JsonCalls.Should().HaveCount(1);
        output.BoxCalls.Should().BeEmpty();
    }

    [Fact]
    public void System_HumanMode_EmitsBox_NotJson()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, new SystemInfo(500, 3, 0, null));

        var code = SystemCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.BoxCalls.Should().HaveCount(1);
        output.JsonCalls.Should().BeEmpty();
    }

    [Fact]
    public void System_ShardFailure_ReturnsExitCode1()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Failure<SystemInfo>(target, "connection refused");

        var code = SystemCommand.Render(output, [target], results, json: false);

        code.Should().Be(1);
        output.ErrorCalls.Should().ContainSingle(e => e.Contains("connection refused"));
    }

    // ── TenantsCommand ───────────────────────────────────────────────────────────

    [Fact]
    public void Tenants_JsonMode_EmitsJson_NotTable()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var lease = new AdminTenantLease("tenant-a", "consumer-1", null, null);
        var results = Success(target, new TenantLeaseInventory(AdminTenancyMode.MultiTenant, [lease]));

        var code = TenantsCommand.Render(output, [target], results, json: true);

        code.Should().Be(0);
        output.JsonCalls.Should().HaveCount(1);
        output.TableCalls.Should().BeEmpty();
    }

    [Fact]
    public void Tenants_HumanMode_EmitsTable_NotJson()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var lease = new AdminTenantLease("tenant-a", "consumer-1", null, null);
        var results = Success(target, new TenantLeaseInventory(AdminTenancyMode.MultiTenant, [lease]));

        var code = TenantsCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().HaveCount(1);
        output.JsonCalls.Should().BeEmpty();
    }

    [Fact]
    public void Tenants_SingleTenantMode_EmitsText_NotTable()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, new TenantLeaseInventory(AdminTenancyMode.SingleTenant, []));

        var code = TenantsCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().BeEmpty();
        output.TextCalls.Should().ContainSingle(t => t.Contains("single-tenant"));
    }

    [Fact]
    public void Tenants_MultiTenantMode_NoLeases_EmitsNoLeasesText()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Success(target, new TenantLeaseInventory(AdminTenancyMode.MultiTenant, []));

        var code = TenantsCommand.Render(output, [target], results, json: false);

        code.Should().Be(0);
        output.TableCalls.Should().BeEmpty();
        output.TextCalls.Should().ContainSingle(t => t.Contains("No active tenant leases"));
    }

    [Fact]
    public void Tenants_ShardFailure_ReturnsExitCode1()
    {
        var output = new TestOutput();
        var target = new ShardTarget(null, "Host=localhost", "public");
        var results = Failure<TenantLeaseInventory>(target, "connection refused");

        var code = TenantsCommand.Render(output, [target], results, json: false);

        code.Should().Be(1);
        output.ErrorCalls.Should().ContainSingle(e => e.Contains("connection refused"));
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
