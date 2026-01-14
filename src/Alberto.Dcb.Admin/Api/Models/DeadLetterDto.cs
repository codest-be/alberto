namespace Alberto.Dcb.Admin.Api.Models;

/// <summary>
/// Dead letter entry information.
/// </summary>
public sealed record DeadLetterDto(
    Guid Id,
    string ProcessorId,
    Guid EventId,
    string EventType,
    string EventData,
    string ErrorMessage,
    string? StackTrace,
    int AttemptCount,
    DateTimeOffset FailedAt);
