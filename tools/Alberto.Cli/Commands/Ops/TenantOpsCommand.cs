using System.CommandLine;
using Alberto.Cli.Output;
using Alberto.Dcb.Postgres;
using Npgsql;
using Spectre.Console;

namespace Alberto.Cli.Commands.Ops;

public static class TenantOpsCommand
{
    public static Command Build()
    {
        var command = new Command("tenants", "Manage tenant leases");

        command.AddCommand(BuildRelease());

        return command;
    }

    private static Command BuildRelease()
    {
        var command = new Command("release", "Release tenant leases, forcing the application to reacquire them");

        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var processorIdOption = new Option<string?>("--processor-id") { Description = "Release leases only for this consumer ID" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };

        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(processorIdOption);
        command.AddOption(yesOption);

        command.SetHandler(async (string? url, string? schema, string? processorId, bool yes) =>
        {
            IOutput output = new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            var scope = processorId is not null
                ? $"consumer '{processorId}'"
                : "all consumers";

            if (!yes)
            {
                var confirmed = AnsiConsole.Confirm(
                    $"Release tenant leases for {scope}? The running application will reacquire them.",
                    defaultValue: false);

                if (!confirmed)
                {
                    output.Text("Aborted.");
                    return;
                }
            }

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var admin = new PostgresAdminDataAccess(dataSource, schemaName);

                var deleted = await admin.ReleaseTenantLeasesAsync(processorId);
                output.Text($"Released {deleted} tenant lease(s) for {scope}.");
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, urlOption, schemaOption, processorIdOption, yesOption);

        return command;
    }
}
