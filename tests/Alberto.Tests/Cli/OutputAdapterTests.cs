using Alberto.Cli.Output;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Cli;

/// <summary>
/// Tests for <see cref="HumanOutput"/> and <see cref="JsonOutput"/> — the two IOutput adapters.
/// Tests cover the methods that write directly to <see cref="Console.Out"/> /
/// <see cref="Console.Error"/>; methods that go through Spectre.Console's internal console (like
/// <see cref="HumanOutput.Text"/> and table rendering) are not tested here because Spectre caches
/// its console reference and Console.SetOut is unreliable for them.
/// </summary>
public class OutputAdapterTests
{
    // ── JsonOutput ───────────────────────────────────────────────────────────────

    [Fact]
    public void JsonOutput_Json_Writes_Serialized_Json_To_Stdout()
    {
        var writer = new StringWriter();
        CaptureStdOut(writer, () =>
        {
            var output = new JsonOutput();
            output.Json(new { name = "test", value = 42 });
        });

        var text = writer.ToString();
        text.Should().Contain("\"name\"");
        text.Should().Contain("\"test\"");
        text.Should().Contain("42");
    }

    [Fact]
    public void JsonOutput_Text_Is_NoOp_And_Produces_No_Output()
    {
        var writer = new StringWriter();
        CaptureStdOut(writer, () =>
        {
            var output = new JsonOutput();
            output.Text("should not appear");
        });

        writer.ToString().Should().BeEmpty();
    }

    [Fact]
    public void JsonOutput_Table_Is_NoOp_And_Produces_No_Output()
    {
        var writer = new StringWriter();
        CaptureStdOut(writer, () =>
        {
            var output = new JsonOutput();
            output.Table(["Col1", "Col2"], [["a", "b"]]);
        });

        writer.ToString().Should().BeEmpty();
    }

    [Fact]
    public void JsonOutput_Box_Is_NoOp_And_Produces_No_Output()
    {
        var writer = new StringWriter();
        CaptureStdOut(writer, () =>
        {
            var output = new JsonOutput();
            output.Box("Title", new Dictionary<string, string> { ["Key"] = "Value" });
        });

        writer.ToString().Should().BeEmpty();
    }

    [Fact]
    public void JsonOutput_Error_Writes_To_Stderr_With_Prefix()
    {
        var writer = new StringWriter();
        CaptureStdErr(writer, () =>
        {
            var output = new JsonOutput();
            output.Error("something went wrong");
        });

        writer.ToString().Should().Contain("something went wrong");
    }

    [Fact]
    public void JsonOutput_Warning_Writes_To_Stderr_With_Prefix()
    {
        var writer = new StringWriter();
        CaptureStdErr(writer, () =>
        {
            var output = new JsonOutput();
            output.Warning("watch out");
        });

        writer.ToString().Should().Contain("watch out");
    }

    // ── HumanOutput ──────────────────────────────────────────────────────────────

    [Fact]
    public void HumanOutput_Error_Writes_To_Stderr_With_Prefix()
    {
        var writer = new StringWriter();
        CaptureStdErr(writer, () =>
        {
            var output = new HumanOutput();
            output.Error("human error message");
        });

        // HumanOutput.Error writes "error: {message}"
        var text = writer.ToString();
        text.Should().Contain("error:");
        text.Should().Contain("human error message");
    }

    [Fact]
    public void HumanOutput_Warning_Writes_To_Stderr_With_Prefix()
    {
        var writer = new StringWriter();
        CaptureStdErr(writer, () =>
        {
            var output = new HumanOutput();
            output.Warning("watch out here");
        });

        var text = writer.ToString();
        text.Should().Contain("warning:");
        text.Should().Contain("watch out here");
    }

    [Fact]
    public void HumanOutput_Json_Writes_To_Stdout()
    {
        var writer = new StringWriter();
        CaptureStdOut(writer, () =>
        {
            var output = new HumanOutput();
            output.Json(new { key = "value" });
        });

        var text = writer.ToString();
        text.Should().Contain("key");
        text.Should().Contain("value");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static void CaptureStdOut(StringWriter writer, Action action)
    {
        var saved = Console.Out;
        Console.SetOut(writer);
        try { action(); }
        finally { Console.SetOut(saved); }
    }

    private static void CaptureStdErr(StringWriter writer, Action action)
    {
        var saved = Console.Error;
        Console.SetError(writer);
        try { action(); }
        finally { Console.SetError(saved); }
    }
}
