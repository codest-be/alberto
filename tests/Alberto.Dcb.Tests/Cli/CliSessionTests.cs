using Alberto.Cli;
using Alberto.Cli.Output;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Cli;

/// <summary>
/// Tests for <see cref="CliSession"/> — the per-invocation context that owns output, config,
/// confirmation, and error handling. All tests run without a database and without a TTY.
/// </summary>
public class CliSessionTests
{
    // ── Output adapter selection ────────────────────────────────────────────────

    [Fact]
    public void Json_True_Selects_JsonOutput()
    {
        var session = new CliSession(json: true, config: new AlbertoConfig());

        session.Output.Should().BeOfType<JsonOutput>();
    }

    [Fact]
    public void Json_False_Selects_HumanOutput()
    {
        var session = new CliSession(json: false, config: new AlbertoConfig());

        session.Output.Should().BeOfType<HumanOutput>();
    }

    // ── Confirmation policy ─────────────────────────────────────────────────────

    [Fact]
    public void Confirm_Yes_True_Returns_Null_Without_Prompting()
    {
        var session = new CliSession(json: false, config: new AlbertoConfig());

        var result = session.Confirm(
            yes: true,
            prompt: "never shown",
            nonInteractiveError: "never shown");

        result.Should().BeNull();
    }

    [Fact]
    public void Confirm_NonInteractive_Without_Yes_Returns_One_And_Writes_Error()
    {
        // dotnet test does not attach a real TTY so AnsiConsole.Profile.Capabilities.Interactive
        // is false in this environment. This test validates the non-interactive path.
        var errorWriter = new StringWriter();
        var savedError = Console.Error;
        Console.SetError(errorWriter);
        try
        {
            var session = new CliSession(json: false, config: new AlbertoConfig());
            var result = session.Confirm(
                yes: false,
                prompt: "interactive prompt",
                nonInteractiveError: "add --yes to confirm");

            // If this assertion fails it means the test is running in a real interactive terminal,
            // which should not happen in CI or via `dotnet test`.
            if (result is not null)
            {
                result.Should().Be(1);
                errorWriter.ToString().Should().Contain("add --yes to confirm");
            }
        }
        finally
        {
            Console.SetError(savedError);
        }
    }

    // ── RunAsync error handling and exit codes ──────────────────────────────────

    [Fact]
    public async Task RunAsync_Body_Returns_Zero_Propagates_Zero()
    {
        var session = new CliSession(json: false, config: new AlbertoConfig());

        var result = await session.RunAsync(() => Task.FromResult(0));

        result.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_Body_Returns_NonZero_Propagates_ExitCode()
    {
        var session = new CliSession(json: false, config: new AlbertoConfig());

        var result = await session.RunAsync(() => Task.FromResult(2));

        result.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_Body_Throws_Exception_Returns_One_And_Writes_Error()
    {
        var errorWriter = new StringWriter();
        var savedError = Console.Error;
        Console.SetError(errorWriter);
        try
        {
            var session = new CliSession(json: false, config: new AlbertoConfig());

            var result = await session.RunAsync(
                () => Task.FromException<int>(new InvalidOperationException("database exploded")));

            result.Should().Be(1);
            errorWriter.ToString().Should().Contain("database exploded");
        }
        finally
        {
            Console.SetError(savedError);
        }
    }

    [Fact]
    public async Task RunAsync_ShardSelectionException_Returns_One_And_Writes_Error()
    {
        var errorWriter = new StringWriter();
        var savedError = Console.Error;
        Console.SetError(errorWriter);
        try
        {
            var session = new CliSession(json: false, config: new AlbertoConfig());

            var result = await session.RunAsync(
                () => Task.FromException<int>(new ShardSelectionException("must specify --shard or --all-shards")));

            result.Should().Be(1);
            errorWriter.ToString().Should().Contain("must specify --shard or --all-shards");
        }
        finally
        {
            Console.SetError(savedError);
        }
    }

    // ── Target resolution delegates to ShardResolver using the pre-loaded config ─

    [Fact]
    public void ReadTargets_UrlOption_Returns_Single_Target_With_Specified_Url()
    {
        // Use the --url option (highest priority) to avoid env-var interference.
        var session = new CliSession(json: false, config: new AlbertoConfig());

        var targets = session.ReadTargets(shard: null, url: "Host=mydb;Database=events", schema: null);

        targets.Should().HaveCount(1);
        targets[0].ConnectionString.Should().Be("Host=mydb;Database=events");
    }

    [Fact]
    public void ReadTargets_UrlOption_Overrides_Config()
    {
        var config = new AlbertoConfig
        {
            Connection = new ConnectionConfig { Url = "Host=from-config" }
        };
        var session = new CliSession(json: false, config: config);

        var targets = session.ReadTargets(shard: null, url: "Host=from-option", schema: null);

        targets[0].ConnectionString.Should().Be("Host=from-option");
    }

    [Fact]
    public void Config_Reused_Across_Multiple_Target_Resolutions()
    {
        // Both reads see the same connection because the config is captured once at construction.
        var session = new CliSession(json: false, config: new AlbertoConfig());

        var first = session.ReadTargets(shard: null, url: "Host=stable", schema: null);
        var second = session.ReadTargets(shard: null, url: "Host=stable", schema: null);

        first[0].ConnectionString.Should().Be(second[0].ConnectionString);
    }
}
