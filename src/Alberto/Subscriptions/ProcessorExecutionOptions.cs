using Alberto.Configuration;

namespace Alberto.Subscriptions;

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
public sealed record ProcessorExecutionOptions
{
    /// <summary>
    /// Controls how batch dispatch is applied for this processor.
    /// </summary>
    public required ProcessorBatchingMode BatchingMode { get; init; }

    /// <summary>
    /// Maximum number of events processed concurrently. Defaults to 1 (serial).
    /// </summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>Default execution settings used when no explicit settings are declared.</summary>
    public static ProcessorExecutionOptions Default { get; } =
        new() { BatchingMode = ProcessorBatchingMode.Required };
}

/// <summary>
/// Mutable, all-nullable mirror of <see cref="ProcessorExecutionOptions"/> used by the
/// configuration binder. Non-null properties are applied on top of the code-declared defaults.
/// Set keys under <c>Alberto:Modules:{moduleKey}:Processors:{processorId}</c>.
/// </summary>
public sealed class ProcessorExecutionOverrides : IAlbertoOverrides<ProcessorExecutionOptions>
{
    /// <summary>
    /// Overrides <see cref="ProcessorExecutionOptions.BatchingMode"/>. Null leaves the default unchanged.
    /// </summary>
    public ProcessorBatchingMode? BatchingMode { get; set; }

    /// <summary>
    /// Overrides <see cref="ProcessorExecutionOptions.MaxConcurrency"/>. Null leaves the default unchanged.
    /// </summary>
    public int? MaxConcurrency { get; set; }

    /// <inheritdoc />
    public ProcessorExecutionOptions ApplyTo(ProcessorExecutionOptions options) =>
        options with
        {
            BatchingMode = BatchingMode ?? options.BatchingMode,
            MaxConcurrency = MaxConcurrency ?? options.MaxConcurrency,
        };
}

internal sealed record ProcessorExecutionRegistration(
    string ProcessorId,
    ProcessorExecutionOptions Options);
