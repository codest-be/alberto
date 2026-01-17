namespace Alberto.Dcb.Admin.Api.Models;

/// <summary>
/// Status information for an event processor.
/// </summary>
public sealed record ProcessorStatusDto(
    string ProcessorId,
    bool IsActive,
    bool IsRebuilding,
    long? LastPosition,
    long GlobalPosition,
    long Lag,
    DateTimeOffset? LastUpdated,
    IReadOnlySet<string> HandledEventTypes,
    int DeadLetterCount);
