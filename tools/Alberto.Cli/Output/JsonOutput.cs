using System.Text.Json;

namespace Alberto.Cli.Output;

public class JsonOutput : IOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Text(string text)
    {
        // No-op for JSON output mode
    }

    public void Table(string[] headers, IEnumerable<string[]> rows)
    {
        // No-op for JSON output mode — callers should use Json() instead
    }

    public void Box(string title, Dictionary<string, string> fields)
    {
        // No-op for JSON output mode — callers should use Json() instead
    }

    public void Json(object data)
    {
        Console.WriteLine(JsonSerializer.Serialize(data, JsonOptions));
    }

    public void Warning(string message)
    {
        Console.Error.WriteLine($"warning: {message}");
    }

    public void Error(string message)
    {
        Console.Error.WriteLine($"error: {message}");
    }
}
