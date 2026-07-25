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

public static class ConfigFileFinder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Walks up the directory tree from the current working directory looking for .alberto/config.json.
    /// Returns null if not found.
    /// </summary>
    public static AlbertoConfig? Find()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (dir != null)
        {
            var configPath = Path.Combine(dir.FullName, ".alberto", "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    return JsonSerializer.Deserialize<AlbertoConfig>(json, JsonOptions);
                }
                catch
                {
                    // Malformed config — treat as not found
                    return null;
                }
            }

            dir = dir.Parent;
        }

        return null;
    }
}
