using Npgsql;

namespace Alberto.EventStore.Postgres.Migrations;

/// <summary>
/// Strategy for managing EventStore schema migrations.
/// Implement this to integrate with your preferred migration tool (EF Core, Flyway, etc.)
/// </summary>
public interface IMigrationStrategy
{
    /// <summary>
    /// Ensures the EventStore schema exists and is up to date.
    /// Called once during application startup (if enabled).
    /// </summary>
    /// <param name="schema">The schema name (e.g., "orders")</param>
    /// <param name="connectionString">The PostgreSQL connection string</param>
    /// <param name="migrationsDirectory">Optional directory for migration scripts. If null, uses strategy default.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EnsureSchemaAsync(string schema, string connectionString, string? migrationsDirectory = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op migration strategy. Use when you manage migrations externally.
/// </summary>
public class NoMigrationStrategy : IMigrationStrategy
{
    public Task EnsureSchemaAsync(string schema, string connectionString, string? migrationsDirectory = null, CancellationToken cancellationToken = default)
    {
        // Do nothing - user manages migrations externally
        return Task.CompletedTask;
    }
}

/// <summary>
/// Script-based migration strategy. Generates SQL scripts without executing them.
/// Use for review-before-deploy workflows.
/// Generates idempotent migration files to {migrationsDirectory}/EventStore/{schema}/
/// </summary>
public class ScriptOnlyMigrationStrategy(string? defaultOutputDirectory = null) : IMigrationStrategy
{
    public async Task EnsureSchemaAsync(string schema, string connectionString, string? migrationsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        // Use passed directory, then default, then fallback to "./Migrations"
        var outputDirectory = migrationsDirectory ?? defaultOutputDirectory ?? "./Migrations";

        // Load embedded migration templates
        var templates = MigrationTemplateLoader.LoadAll();

        // Generate to disk: {outputDirectory}/EventStore/{schema}/
        var schemaDir = Path.Combine(outputDirectory, "EventStore", schema);
        Directory.CreateDirectory(schemaDir);

        foreach (var (migrationName, template) in templates)
        {
            // Replace {schema} placeholder
            var sql = template.Replace("{schema}", schema);
            var filePath = Path.Combine(schemaDir, $"{migrationName}.sql");

            // Only write if file doesn't exist (don't overwrite user modifications)
            if (!File.Exists(filePath))
            {
                await File.WriteAllTextAsync(filePath, sql, cancellationToken);
                Console.WriteLine($"Generated migration: {filePath}");
            }
        }

        Console.WriteLine($"Migration scripts generated to: {schemaDir}");
        Console.WriteLine($"Review and apply via your deployment pipeline.");
    }
}

/// <summary>
/// Automatic migration strategy.
/// Reads migrations from {migrationsDirectory}/EventStore/{schema}/ and applies them automatically.
/// If migrations don't exist, generates them first.
/// Use only for development/testing. NOT recommended for production.
/// </summary>
public class AutoMigrationStrategy(string? defaultMigrationsDirectory = null) : IMigrationStrategy
{
    public async Task EnsureSchemaAsync(string schema, string connectionString, string? migrationsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        // Use passed directory, then default, then fallback to "./Migrations"
        var effectiveMigrationsDirectory = migrationsDirectory ?? defaultMigrationsDirectory ?? "./Migrations";
        var schemaDir = Path.Combine(effectiveMigrationsDirectory, "EventStore", schema);

        // If migrations don't exist, generate them first
        if (!Directory.Exists(schemaDir) || Directory.GetFiles(schemaDir, "*.sql").Length == 0)
        {
            Console.WriteLine($"Generating migration scripts for schema '{schema}'...");
            await new ScriptOnlyMigrationStrategy(effectiveMigrationsDirectory)
                .EnsureSchemaAsync(schema, connectionString, effectiveMigrationsDirectory, cancellationToken);
        }

        // Load migration files from disk
        var migrationFiles = Directory.GetFiles(schemaDir, "*.sql")
            .OrderBy(Path.GetFileName)
            .ToList();

        if (migrationFiles.Count == 0)
        {
            Console.WriteLine($"No migration files found for schema '{schema}'");
            return;
        }

        // Apply each migration (migrations are idempotent)
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var file in migrationFiles)
        {
            var migrationName = Path.GetFileNameWithoutExtension(file);
            var sql = await File.ReadAllTextAsync(file, cancellationToken);

            Console.WriteLine($"Applying migration: {migrationName}");

            await using var command = new NpgsqlCommand(sql, connection);
            command.CommandTimeout = 300; // 5 minutes for large migrations
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        Console.WriteLine($"All migrations applied for schema '{schema}'");
    }
}