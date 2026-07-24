namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Controls how a processor should execute inside the async control loop.
/// </summary>
public enum ProcessorBatchingMode
{
    /// <summary>
    /// Always use the current per-event dispatch path.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Use batch dispatch when the processor supports it; otherwise fall back to per-event dispatch.
    /// </summary>
    IfSupported = 1,

    /// <summary>
    /// Require batch dispatch and fail fast if the processor does not implement <see cref="IBatchableProcessor"/>.
    /// </summary>
    Required = 2,
}

/// <summary>
/// Immutable execution settings attached to a processor registration.
/// </summary>
public sealed record ProcessorExecutionOptions(
    ProcessorBatchingMode BatchingMode,
    int MaxConcurrency = 1)
{
    public static ProcessorExecutionOptions Default { get; } =
        new(ProcessorBatchingMode.Required);
}


internal sealed record ProcessorExecutionRegistration(
    string ProcessorId,
    ProcessorExecutionOptions Options);
