namespace Alberto.Dcb.Append;

/// <summary>
/// Pipeline for executing append interceptors around the actual persistence operation.
/// </summary>
public interface IAppendInterceptorPipeline
{
    /// <summary>
    /// Executes the append interceptor pipeline.
    /// </summary>
    /// <param name="context">The append context.</param>
    /// <param name="appendAction">The actual append action to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended events with assigned global positions.</returns>
    Task<IReadOnlyCollection<IEventEnvelope>> ExecuteAsync(
        AppendContext context,
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>> appendAction,
        CancellationToken ct = default);
}
