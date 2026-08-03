using Alberto.Admin;
using Alberto.Postgres;
using Npgsql;

namespace Alberto.Cli;

/// <summary>
/// Constructs admin interface adapters from a data source and schema. Every CLI construction
/// site for the admin surface routes through here so the concrete Postgres types stay behind
/// one seam.
/// </summary>
internal static class AdminAdapters
{
    public static IAdminReader Reader(NpgsqlDataSource dataSource, string schema) =>
        new PostgresAdminDataAccess(dataSource, schema);

    public static IAdminOperator Operator(NpgsqlDataSource dataSource, string schema) =>
        new PostgresAdminOperator(dataSource, schema);
}
