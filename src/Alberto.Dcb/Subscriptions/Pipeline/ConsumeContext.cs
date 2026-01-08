namespace Alberto.Dcb.Subscriptions.Pipeline;

/// <summary>
/// Context passed through the consume pipeline, providing information about the current processing operation.
/// </summary>
/// <param name="ProcessorId">The identifier of the processor handling the events.</param>
/// <param name="ModuleKey">The module key for service resolution.</param>
/// <param name="CurrentCheckpoint">The current checkpoint position before processing.</param>
public sealed record ConsumeContext(
    string ProcessorId,
    string ModuleKey,
    long? CurrentCheckpoint);
