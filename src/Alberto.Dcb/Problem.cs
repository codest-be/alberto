namespace Alberto.Dcb;

public sealed record Problem
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public IReadOnlyDictionary<string, object> Details { get; init; } = new Dictionary<string, object>();

    public static Problem Create(
        string code,
        string message,
        IReadOnlyDictionary<string, object>? details = null) =>
        new()
        {
            Code = code,
            Message = message,
            Details = details ?? new Dictionary<string, object>()
        };
}
