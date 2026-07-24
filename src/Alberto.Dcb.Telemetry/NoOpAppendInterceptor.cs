using Alberto.Dcb.Append;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// No-op append interceptor used when telemetry is disabled.
/// Delegates directly to the next handler without creating activities or recording metrics.
/// </summary>
internal sealed class NoOpAppendInterceptor : IAppendInterceptor
{
    internal static readonly NoOpAppendInterceptor Instance = new();

    private NoOpAppendInterceptor() { }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<IEventEnvelope>> OnAppendingAsync(
        AppendContext context,
        Func<AppendContext, Task<IReadOnlyCollection<IEventEnvelope>>> next,
        CancellationToken ct = default) => next(context);
}
