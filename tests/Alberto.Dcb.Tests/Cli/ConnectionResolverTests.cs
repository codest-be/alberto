using Alberto.Cli;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Cli;

/// <summary>
/// Tests for <see cref="ConnectionResolver"/> — the priority-ordered connection string resolver.
/// Uses the config-accepting overloads so no file I/O occurs. Environment variable tests restore
/// their state in finally blocks; tests within a class run sequentially in xUnit so there is no
/// inter-test variable leakage within this file.
/// </summary>
public class ConnectionResolverTests
{
    // ── ResolveConnectionString priority chain ───────────────────────────────────

    [Fact]
    public void Explicit_Url_Option_Wins_Over_All_Others()
    {
        WithEnv("ALBERTO_URL", "Host=from-env", () =>
        {
            var config = new AlbertoConfig { Connection = new ConnectionConfig { Url = "Host=from-config" } };

            var result = ConnectionResolver.ResolveConnectionString(config, "Host=from-option");

            result.Should().Be("Host=from-option");
        });
    }

    [Fact]
    public void ALBERTO_URL_Env_Wins_Over_Config()
    {
        ClearConnectionEnvVars();
        WithEnv("ALBERTO_URL", "Host=from-env", () =>
        {
            var config = new AlbertoConfig { Connection = new ConnectionConfig { Url = "Host=from-config" } };

            var result = ConnectionResolver.ResolveConnectionString(config, urlOption: null);

            result.Should().Be("Host=from-env");
        });
    }

    [Fact]
    public void Config_Url_Used_When_No_Option_Or_ALBERTO_URL()
    {
        ClearConnectionEnvVars();
        var config = new AlbertoConfig { Connection = new ConnectionConfig { Url = "Host=from-config" } };

        var result = ConnectionResolver.ResolveConnectionString(config, urlOption: null);

        result.Should().Be("Host=from-config");
    }

    [Fact]
    public void DATABASE_URL_Env_Is_Fallback_When_No_Config_Url()
    {
        ClearConnectionEnvVars();
        WithEnv("DATABASE_URL", "Host=from-database-url", () =>
        {
            var result = ConnectionResolver.ResolveConnectionString(config: null, urlOption: null);

            result.Should().Be("Host=from-database-url");
        });
    }

    [Fact]
    public void Returns_Default_Localhost_When_Nothing_Is_Set()
    {
        ClearConnectionEnvVars();
        var result = ConnectionResolver.ResolveConnectionString(config: null, urlOption: null);

        result.Should().Be("Host=localhost;Database=postgres");
    }

    [Fact]
    public void Empty_Config_Url_Falls_Through_To_Database_Url_Env()
    {
        ClearConnectionEnvVars();
        WithEnv("DATABASE_URL", "Host=fallback", () =>
        {
            var config = new AlbertoConfig { Connection = new ConnectionConfig { Url = "" } };

            var result = ConnectionResolver.ResolveConnectionString(config, urlOption: null);

            result.Should().Be("Host=fallback");
        });
    }

    // ── ResolveSchema priority chain ─────────────────────────────────────────────

    [Fact]
    public void ResolveSchema_Option_Wins_Over_Config()
    {
        var config = new AlbertoConfig { Schema = "from-config" };

        ConnectionResolver.ResolveSchema(config, "from-option").Should().Be("from-option");
    }

    [Fact]
    public void ResolveSchema_Config_Used_When_No_Option()
    {
        var config = new AlbertoConfig { Schema = "myschema" };

        ConnectionResolver.ResolveSchema(config, schemaOption: null).Should().Be("myschema");
    }

    [Fact]
    public void ResolveSchema_Returns_Public_As_Default()
    {
        ConnectionResolver.ResolveSchema(config: null, schemaOption: null).Should().Be("public");
    }

    [Fact]
    public void ResolveSchema_Empty_Config_Schema_Falls_Through_To_Default()
    {
        var config = new AlbertoConfig { Schema = "" };

        ConnectionResolver.ResolveSchema(config, schemaOption: null).Should().Be("public");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static void ClearConnectionEnvVars()
    {
        Environment.SetEnvironmentVariable("ALBERTO_URL", null);
        Environment.SetEnvironmentVariable("DATABASE_URL", null);
    }

    private static void WithEnv(string key, string value, Action action)
    {
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
        try { action(); }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }
}
