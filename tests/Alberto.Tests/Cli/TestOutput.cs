using Alberto.Cli.Output;

namespace Alberto.Tests.Cli;

/// <summary>
/// Test double that records every IOutput call so tests can assert on what was written.
/// </summary>
/// <remarks>
/// <para>
/// The shipped adapters cannot be observed from a test. HumanOutput routes Text/Table/Box through
/// Spectre's internal console, which ignores <see cref="Console.SetOut"/>; and everything either
/// adapter writes goes to <see cref="Console.Out"/> or <see cref="Console.Error"/>, which belong to
/// the process rather than to a test. Redirecting those makes a test read whatever its parallel
/// siblings happened to emit.
/// </para>
/// <para>
/// This double is per-instance, so each test reads back only its own output.
/// </para>
/// </remarks>
internal sealed class TestOutput : IOutput
{
    public List<object> JsonCalls { get; } = [];
    public List<string> TextCalls { get; } = [];
    public List<string> ErrorCalls { get; } = [];
    public List<string> WarningCalls { get; } = [];
    public List<(string[] Headers, string[][] Rows)> TableCalls { get; } = [];
    public List<(string Title, Dictionary<string, string> Fields)> BoxCalls { get; } = [];

    public void Text(string text) => TextCalls.Add(text);

    public void Table(string[] headers, IEnumerable<string[]> rows) =>
        TableCalls.Add((headers, rows.ToArray()));

    public void Box(string title, Dictionary<string, string> fields) =>
        BoxCalls.Add((title, fields));

    public void Json(object data) => JsonCalls.Add(data);
    public void Warning(string message) => WarningCalls.Add(message);
    public void Error(string message) => ErrorCalls.Add(message);
}
