namespace Alberto.Dcb.Admin.Api.Models;

/// <summary>
/// Status of a projection rebuild operation.
/// </summary>
public sealed record RebuildStatus(
    string ProcessorId,
    RebuildState State,
    long CurrentPosition,
    long TargetPosition,
    double ProgressPercent,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);

/// <summary>
/// State of a rebuild operation.
/// </summary>
public enum RebuildState
{
    NotStarted,
    Clearing,
    Rebuilding,
    Completed,
    Failed,
    Cancelled
}
