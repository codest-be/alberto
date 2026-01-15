using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Orders.Api.GraphQL.Types;

/// <summary>
/// GraphQL type for processor status.
/// </summary>
[GraphQLDescription("Status information for an event processor.")]
public sealed record ProcessorStatus(
    [property: GraphQLDescription("Unique identifier of the processor.")]
    string ProcessorId,

    [property: GraphQLDescription("Whether the processor is currently active.")]
    bool IsActive,

    [property: GraphQLDescription("Last processed event position, null if never processed.")]
    long? LastPosition,

    [property: GraphQLDescription("Current global position in the event stream.")]
    long GlobalPosition,

    [property: GraphQLDescription("Number of events behind the global position.")]
    long Lag,

    [property: GraphQLDescription("Last checkpoint update time.")]
    DateTimeOffset? LastUpdated,

    [property: GraphQLDescription("Event types handled by this processor.")]
    IReadOnlyList<string> HandledEventTypes,

    [property: GraphQLDescription("Number of dead letters for this processor.")]
    int DeadLetterCount)
{
    public static ProcessorStatus FromDto(ProcessorStatusDto dto) => new(
        dto.ProcessorId,
        dto.IsActive,
        dto.LastPosition,
        dto.GlobalPosition,
        dto.Lag,
        dto.LastUpdated,
        dto.HandledEventTypes.ToList(),
        dto.DeadLetterCount);
}

/// <summary>
/// GraphQL type for processor status update event.
/// </summary>
[GraphQLDescription("Real-time processor status update.")]
public sealed record ProcessorStatusUpdated(
    [property: GraphQLDescription("Module key for this processor.")]
    string ModuleKey,

    [property: GraphQLDescription("Updated processor status.")]
    ProcessorStatus Processor);

/// <summary>
/// GraphQL type for checkpoint.
/// </summary>
[GraphQLDescription("Checkpoint information for a processor.")]
public sealed record Checkpoint(
    [property: GraphQLDescription("Processor identifier.")]
    string ProcessorId,

    [property: GraphQLDescription("Last processed event position.")]
    long LastPosition,

    [property: GraphQLDescription("Last checkpoint update time.")]
    DateTimeOffset UpdatedAt)
{
    public static Checkpoint FromDto(CheckpointDto dto) => new(
        dto.ProcessorId,
        dto.LastPosition,
        dto.UpdatedAt);
}

/// <summary>
/// GraphQL type for checkpoint update event.
/// </summary>
[GraphQLDescription("Real-time checkpoint update.")]
public sealed record CheckpointUpdated(
    [property: GraphQLDescription("Module key.")]
    string ModuleKey,

    [property: GraphQLDescription("Updated checkpoint.")]
    Checkpoint Checkpoint);

/// <summary>
/// GraphQL type for dead letter.
/// </summary>
[GraphQLDescription("Dead letter event that failed processing.")]
public sealed record DeadLetter(
    [property: GraphQLDescription("Unique identifier.")]
    Guid Id,

    [property: GraphQLDescription("Processor that failed to process this event.")]
    string ProcessorId,

    [property: GraphQLDescription("Original event identifier.")]
    Guid EventId,

    [property: GraphQLDescription("Type of the event.")]
    string EventType,

    [property: GraphQLDescription("Error message from the failure.")]
    string ErrorMessage,

    [property: GraphQLDescription("Number of processing attempts.")]
    int AttemptCount,

    [property: GraphQLDescription("Time when the event was moved to dead letters.")]
    DateTimeOffset FailedAt)
{
    public static DeadLetter FromDto(DeadLetterDto dto) => new(
        dto.Id,
        dto.ProcessorId,
        dto.EventId,
        dto.EventType,
        dto.ErrorMessage,
        dto.AttemptCount,
        dto.FailedAt);
}

/// <summary>
/// GraphQL type for dead letter change event.
/// </summary>
[GraphQLDescription("Real-time dead letter change.")]
public sealed record DeadLetterChanged(
    [property: GraphQLDescription("Module key.")]
    string ModuleKey,

    [property: GraphQLDescription("The dead letter.")]
    DeadLetter DeadLetter,

    [property: GraphQLDescription("Type of change (Added or Removed).")]
    string ChangeType);

/// <summary>
/// GraphQL type for system info.
/// </summary>
[GraphQLDescription("System information and statistics.")]
public sealed record SystemInfo(
    [property: GraphQLDescription("Module key.")]
    string ModuleKey,

    [property: GraphQLDescription("Current global event position.")]
    long GlobalPosition,

    [property: GraphQLDescription("Number of registered processors.")]
    int ProcessorCount,

    [property: GraphQLDescription("Total dead letter count.")]
    int DeadLetterCount,

    [property: GraphQLDescription("Whether the admin panel is in read-only mode.")]
    bool ReadOnlyMode)
{
    public static SystemInfo FromDto(SystemInfoDto dto) => new(
        dto.ModuleKey,
        dto.GlobalPosition,
        dto.ProcessorCount,
        dto.DeadLetterCount,
        dto.ReadOnlyMode);
}

/// <summary>
/// GraphQL type for system info update event.
/// </summary>
[GraphQLDescription("Real-time system info update.")]
public sealed record SystemInfoUpdated(
    [property: GraphQLDescription("Module key.")]
    string ModuleKey,

    [property: GraphQLDescription("Updated system info.")]
    SystemInfo Info);
