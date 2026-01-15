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
