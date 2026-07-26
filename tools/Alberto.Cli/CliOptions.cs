using System.CommandLine;

namespace Alberto.Cli;

/// <summary>
/// Shared CLI option registration helpers — one call instead of six repeated lines.
/// </summary>
/// <remarks>
/// Follow <c>ShardsCommand.AddCatalogOptions</c> as the model: each helper adds its options to the
/// command and returns the symbols for use in the handler. This is the existing precedent in the
/// codebase; <c>CliOptions</c> generalises it for the standard connection triple.
/// </remarks>
public static class CliOptions
{
    /// <summary>
    /// Adds the standard <c>--url</c> / <c>--schema</c> / <c>--json</c> option triple to a command
    /// and returns the three symbols for use in the handler.
    /// </summary>
    public static (Option<string?> Url, Option<string?> Schema, Option<bool> Json)
        AddConnectionOptions(Command command)
    {
        var url = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schema = new Option<string?>("--schema") { Description = "Database schema name" };
        var json = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddOption(url);
        command.AddOption(schema);
        command.AddOption(json);

        return (url, schema, json);
    }
}
