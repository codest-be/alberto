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

/// <summary>
/// Fluent configurator used by registration helpers such as <c>ReactTo(..., configure: ...)</c>.
/// </summary>
public sealed class ProcessorExecutionConfigurator
{
    private ProcessorBatchingMode _batchingMode = ProcessorBatchingMode.Required;
    private int _maxConcurrency = 1;

    /// <summary>
    /// Prefer batch dispatch when the processor supports it.
    /// Falls back to the normal per-event path otherwise, including when
    /// per-event consume middleware is configured.
    /// </summary>
    public ProcessorExecutionConfigurator BatchIfSupported()
    {
        _batchingMode = ProcessorBatchingMode.IfSupported;
        return this;
    }

    /// <summary>
    /// Require batch dispatch and fail fast if the processor is not batch-capable
    /// or if the runtime cannot preserve configured middleware semantics.
    /// </summary>
    public ProcessorExecutionConfigurator RequireBatching()
    {
        _batchingMode = ProcessorBatchingMode.Required;
        return this;
    }

    /// <summary>
    /// Force the default per-event execution path.
    /// </summary>
    public ProcessorExecutionConfigurator DisableBatching()
    {
        _batchingMode = ProcessorBatchingMode.Disabled;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of events processed concurrently within a batch.
    /// Default is 1 (sequential). Requires batching to be enabled.
    /// </summary>
    public ProcessorExecutionConfigurator WithConcurrency(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        _maxConcurrency = maxConcurrency;
        return this;
    }

    internal ProcessorExecutionOptions Build()
    {
        if (_maxConcurrency > 1 && _batchingMode == ProcessorBatchingMode.Disabled)
        {
            throw new InvalidOperationException(
                "WithConcurrency requires batching to be enabled. " +
                "Call RequireBatching() or BatchIfSupported() first.");
        }

        return new(_batchingMode, _maxConcurrency);
    }
}

internal sealed record ProcessorExecutionRegistration(
    string ProcessorId,
    ProcessorExecutionOptions Options);
