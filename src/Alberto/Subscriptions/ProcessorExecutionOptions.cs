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
/// How a processor's consume span relates to the trace that appended the event it handles.
/// </summary>
public enum TraceParenting
{
    /// <summary>
    /// The consume span starts a new trace and carries a link to the append trace. Right for
    /// projections, which fan in from many unrelated appends and run long after them.
    /// </summary>
    Link = 0,

    /// <summary>
    /// The consume span is a child of the append trace, so the whole flow is one trace and head
    /// sampling keeps or drops it as a unit. Right for reactors. Batch dispatch has many parents
    /// and so still links, one link per distinct upstream trace.
    /// </summary>
    Parent = 1,
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

    /// <summary>
    /// How the consume span relates to the append trace. Defaults to <see cref="TraceParenting.Link"/>;
    /// reactors and the outbox handler are declared with <see cref="TraceParenting.Parent"/>.
    /// </summary>
    public TraceParenting TraceParenting { get; init; } = TraceParenting.Link;

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

    /// <summary>
    /// Overrides <see cref="ProcessorExecutionOptions.TraceParenting"/>. Null leaves the default unchanged.
    /// </summary>
    public TraceParenting? TraceParenting { get; set; }

    /// <inheritdoc />
    public ProcessorExecutionOptions ApplyTo(ProcessorExecutionOptions options) =>
        options with
        {
            BatchingMode = BatchingMode ?? options.BatchingMode,
            MaxConcurrency = MaxConcurrency ?? options.MaxConcurrency,
            TraceParenting = TraceParenting ?? options.TraceParenting,
        };
}

internal sealed record ProcessorExecutionRegistration(
    string ProcessorId,
    ProcessorExecutionOptions Options);
