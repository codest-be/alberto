using Alberto.Cli.Output;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Cli;

/// <summary>
/// Tests for <see cref="HumanOutput"/> and <see cref="JsonOutput"/> — the two IOutput adapters.
/// Tests cover the methods that write to stdout / stderr; methods that go through Spectre.Console's
/// internal console (like <see cref="HumanOutput.Text"/> and table rendering) are not tested here
/// because Spectre caches its console reference and neither these writers nor Console.SetOut
/// reach it.
/// </summary>
/// <remarks>
/// Each test hands the adapter its own writers rather than redirecting the process console.
/// <see cref="Console.Out"/> belongs to the process, not to a test, and xUnit runs collections in
/// parallel — so for as long as a redirect is in place, everything the rest of the suite emits
/// lands in the capture. That is not a hazard in principle: it failed
/// <c>JsonOutput_Box_Is_NoOp_And_Produces_No_Output</c> on a DbUp migration logging its progress
/// from another test's container. The <c>Contain</c> assertions were quietly worse off — foreign
/// output cannot fail them, so they would have passed on an adapter that wrote nothing at all.
/// </remarks>
public class OutputAdapterTests
{
    // ── JsonOutput ───────────────────────────────────────────────────────────────

    [Fact]
    public void JsonOutput_Json_Writes_Serialized_Json_To_Stdout()
    {
        var (output, stdout, _) = JsonAdapter();

        output.Json(new { name = "test", value = 42 });

        var text = stdout.ToString();
        text.Should().Contain("\"name\"");
        text.Should().Contain("\"test\"");
        text.Should().Contain("42");
    }

    [Fact]
    public void JsonOutput_Text_Is_NoOp_And_Produces_No_Output()
    {
        var (output, stdout, stderr) = JsonAdapter();

        output.Text("should not appear");

        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().BeEmpty();
    }

    [Fact]
    public void JsonOutput_Table_Is_NoOp_And_Produces_No_Output()
    {
        var (output, stdout, stderr) = JsonAdapter();

        output.Table(["Col1", "Col2"], [["a", "b"]]);

        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().BeEmpty();
    }

    [Fact]
    public void JsonOutput_Box_Is_NoOp_And_Produces_No_Output()
    {
        var (output, stdout, stderr) = JsonAdapter();

        output.Box("Title", new Dictionary<string, string> { ["Key"] = "Value" });

        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().BeEmpty();
    }

    [Fact]
    public void JsonOutput_Error_Writes_To_Stderr_With_Prefix()
    {
        var (output, stdout, stderr) = JsonAdapter();

        output.Error("something went wrong");

        stderr.ToString().Should().Contain("something went wrong");
        stdout.ToString().Should().BeEmpty();
    }

    [Fact]
    public void JsonOutput_Warning_Writes_To_Stderr_With_Prefix()
    {
        var (output, stdout, stderr) = JsonAdapter();

        output.Warning("watch out");

        stderr.ToString().Should().Contain("watch out");
        stdout.ToString().Should().BeEmpty();
    }

    // ── HumanOutput ──────────────────────────────────────────────────────────────

    [Fact]
    public void HumanOutput_Error_Writes_To_Stderr_With_Prefix()
    {
        var (output, stdout, stderr) = HumanAdapter();

        output.Error("human error message");

        // HumanOutput.Error writes "error: {message}"
        var text = stderr.ToString();
        text.Should().Contain("error:");
        text.Should().Contain("human error message");
        stdout.ToString().Should().BeEmpty();
    }

    [Fact]
    public void HumanOutput_Warning_Writes_To_Stderr_With_Prefix()
    {
        var (output, stdout, stderr) = HumanAdapter();

        output.Warning("watch out here");

        var text = stderr.ToString();
        text.Should().Contain("warning:");
        text.Should().Contain("watch out here");
        stdout.ToString().Should().BeEmpty();
    }

    [Fact]
    public void HumanOutput_Json_Writes_To_Stdout()
    {
        var (output, stdout, stderr) = HumanAdapter();

        output.Json(new { key = "value" });

        var text = stdout.ToString();
        text.Should().Contain("key");
        text.Should().Contain("value");
        stderr.ToString().Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static (JsonOutput Output, StringWriter Stdout, StringWriter Stderr) JsonAdapter()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        return (new JsonOutput(stdout, stderr), stdout, stderr);
    }

    private static (HumanOutput Output, StringWriter Stdout, StringWriter Stderr) HumanAdapter()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        return (new HumanOutput(stdout, stderr), stdout, stderr);
    }
}
