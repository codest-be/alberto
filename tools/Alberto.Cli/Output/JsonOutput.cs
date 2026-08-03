using System.Text.Json;

namespace Alberto.Cli.Output;

public class JsonOutput : IOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly TextWriter? _stdout;
    private readonly TextWriter? _stderr;

    /// <summary>Writes to the process console.</summary>
    public JsonOutput()
    {
    }

    /// <summary>
    /// Writes to <paramref name="stdout"/> and <paramref name="stderr"/> instead of the process
    /// console. This is what a test uses: <see cref="Console.Out"/> belongs to the process rather
    /// than to a test, so redirecting it makes the test read whatever its parallel siblings
    /// happened to emit.
    /// </summary>
    public JsonOutput(TextWriter stdout, TextWriter stderr)
    {
        _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
        _stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
    }

    // Resolved per write rather than captured in the constructor, so writes to the process
    // console stay as late-bound as they were when these methods called Console directly.
    private TextWriter Stdout => _stdout ?? Console.Out;
    private TextWriter Stderr => _stderr ?? Console.Error;

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
        Stdout.WriteLine(JsonSerializer.Serialize(data, JsonOptions));
    }

    public void Warning(string message)
    {
        Stderr.WriteLine($"warning: {message}");
    }

    public void Error(string message)
    {
        Stderr.WriteLine($"error: {message}");
    }
}
