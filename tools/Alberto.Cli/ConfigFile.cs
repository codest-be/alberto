using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alberto.Cli;

public class AlbertoConfig
{
    [JsonPropertyName("connection")]
    public ConnectionConfig? Connection { get; set; }

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("operator")]
    public string? Operator { get; set; }

    /// <summary>
    /// The module's databases, keyed by shard id, when it is spread over several. Absent for the
    /// single-database default, which is every command's behaviour when this is empty.
    /// </summary>
    [JsonPropertyName("shards")]
    public Dictionary<string, ShardConfig>? Shards { get; set; }

    /// <summary>The control database holding the tenant → shard catalog.</summary>
    [JsonPropertyName("catalog")]
    public CatalogConfig? Catalog { get; set; }

    /// <summary>
    /// The module key the catalog rows are filed under. Must match the key the application passes
    /// to <c>AddAlberto</c>, since it is part of the catalog's primary key.
    /// </summary>
    [JsonPropertyName("module")]
    public string? Module { get; set; }
}

public class ConnectionConfig
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>One shard's database. <c>schema</c> falls back to the top-level <c>schema</c>.</summary>
public class ShardConfig
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }
}

/// <summary>The control database. <c>schema</c> falls back to the top-level <c>schema</c>.</summary>
public class CatalogConfig
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }
}

/// <summary>
/// Raised when a configuration file exists but cannot be trusted. An operator-visible config
/// error must stop the command rather than silently falling back to environment variables or a
/// smaller set of databases.
/// </summary>
public sealed class CliConfigurationException(string message, Exception innerException)
    : Exception(message, innerException);

public static class ConfigFileFinder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Walks up the directory tree from the current working directory looking for .alberto/config.json.
    /// Returns null if not found.
    /// </summary>
    public static AlbertoConfig? Find() =>
        Find(new DirectoryInfo(Directory.GetCurrentDirectory()));

    /// <summary>
    /// Walks up the directory tree from <paramref name="startDir"/> looking for .alberto/config.json.
    /// Returns null if not found. Throws when a file is present but unreadable or invalid, because
    /// treating a broken operator configuration as absent could redirect a command to the wrong
    /// database. Pass a specific directory in tests to avoid touching the real working directory.
    /// </summary>
    public static AlbertoConfig? Find(DirectoryInfo startDir)
    {
        var dir = startDir;

        while (dir != null)
        {
            var configPath = Path.Combine(dir.FullName, ".alberto", "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    return JsonSerializer.Deserialize<AlbertoConfig>(json, JsonOptions)
                        ?? throw new JsonException("The configuration root must be a JSON object.");
                }
                catch (Exception ex) when (
                    ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    throw new CliConfigurationException(
                        $"Could not load Alberto configuration '{configPath}': {ex.Message}",
                        ex);
                }
            }

            dir = dir.Parent;
        }

        return null;
    }
}
