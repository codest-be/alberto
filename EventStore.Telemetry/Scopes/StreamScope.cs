using System.Diagnostics;

namespace EventStore.Telemetry.Scopes;

internal sealed class StreamScope(Activity activity) : IDisposable
{
    private bool _disposed;

    public const string ActivityName = "Alberto.Stream";

    public StreamScope WithQuery(StreamQuery query, int? maxCount)
    {
        activity.DisplayName = $"Stream: {query}";
        activity.SetTag(Tags.MaxCount, maxCount?.ToString() ?? "unlimited");

        return this;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        activity.Dispose();
        _disposed = true;
    }
}