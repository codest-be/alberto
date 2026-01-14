namespace Alberto.Dcb.Admin.Api.Models;

/// <summary>
/// Checkpoint information for a processor.
/// </summary>
public sealed record CheckpointDto(
    string ProcessorId,
    long LastPosition,
    DateTimeOffset UpdatedAt);
