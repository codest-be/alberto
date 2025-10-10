namespace Alberto.EventStore.Postgres.Migrations;

/// <summary>
/// Interface for event store database migrations.
/// Implement this interface to create migrations that can be discovered and executed.
/// </summary>
public interface IEventStoreMigration
{
    /// <summary>
    /// Unique migration number for ordering. Lower numbers run first.
    /// </summary>
    int Number { get; }

    /// <summary>
    /// Unique key identifying this migration.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// The SQL script to execute. Use {schema} placeholder for schema substitution.
    /// </summary>
    string GetSql(string schema);
}