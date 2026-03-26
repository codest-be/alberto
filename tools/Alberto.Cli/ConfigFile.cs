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
}

public class ConnectionConfig
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
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
