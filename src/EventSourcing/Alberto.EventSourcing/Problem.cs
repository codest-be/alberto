namespace Alberto.EventSourcing;

/// <summary>
/// Represents a problem or error that occurred during command/query execution.
/// </summary>
public sealed record Problem
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public Dictionary<string, object> Details { get; init; } = new();

    public static Problem Create(string code, string message, Dictionary<string, object>? details = null) =>
        new() { Code = code, Message = message, Details = details ?? new() };

    public static implicit operator Problem(string message) =>
        Create("Error", message);
}