using System.Diagnostics;

namespace Alberto.EventStore.Telemetry.Scopes;

internal sealed class StreamScope(Activity activity) : IDisposable
{
    public const string ActivityName = "Alberto.Stream";
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;

        activity.Dispose();
        _disposed = true;
    }

    public StreamScope WithQuery(StreamQuery query, int? maxCount)
    {
        activity.DisplayName = $"Stream: {query}";
        activity.SetTag(Tags.MaxCount, maxCount?.ToString() ?? "unlimited");

        return this;
    }
}