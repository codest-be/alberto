namespace Alberto.Dcb.Append;

/// <summary>
/// Defines an interceptor for append operations.
/// Interceptors can enrich events before persistence and execute code around the append operation.
/// Similar to EF Core interceptors.
/// </summary>
public interface IAppendInterceptor
{
    /// <summary>
    /// Called when events are being appended.
    /// </summary>
    /// <param name="context">The append context containing events to persist.</param>
    /// <param name="next">The next interceptor or final append action.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended events with assigned global positions.</returns>
    Task<IReadOnlyCollection<IEventEnvelope>> OnAppendingAsync(
        AppendContext context,
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>> next,
        CancellationToken ct = default);
}
