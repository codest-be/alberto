using System.Reflection;

namespace Alberto.EventStore.Postgres.Migrations;

/// <summary>
/// Loads embedded migration template SQL files from the library.
/// Templates contain {schema} placeholder that gets replaced with actual schema name.
/// </summary>
internal static class MigrationTemplateLoader
{
    private const string TemplateNamespace = "Alberto.EventStore.Postgres.Migrations.Templates";

    /// <summary>
    /// Loads all migration templates from embedded resources.
    /// </summary>
    /// <returns>Dictionary of (migration_name, template_sql)</returns>
    public static Dictionary<string, string> LoadAll()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var templates = new Dictionary<string, string>();

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(TemplateNamespace) && name.EndsWith(".sql"))
            .OrderBy(name => name)
            .ToList();

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                continue;

            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            // Extract migration name from resource name
            // e.g., "Alberto.EventStore.Postgres.Migrations.Templates.001_InitialSchema.sql" -> "001_InitialSchema"
            var migrationName = resourceName
                .Replace(TemplateNamespace + ".", "")
                .Replace(".sql", "");

            templates[migrationName] = sql;
        }

        return templates;
    }

    /// <summary>
    /// Loads a specific migration template by name.
    /// </summary>
    public static string? Load(string migrationName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{TemplateNamespace}.{migrationName}.sql";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Gets all available migration names in order.
    /// </summary>
    public static List<string> GetMigrationNames()
    {
        var assembly = Assembly.GetExecutingAssembly();

        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(TemplateNamespace) && name.EndsWith(".sql"))
            .Select(name => name.Replace(TemplateNamespace + ".", "").Replace(".sql", ""))
            .OrderBy(name => name)
            .ToList();
    }
}