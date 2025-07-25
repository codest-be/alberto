using System.Diagnostics;
using EventStore.Events;

namespace EventStore.Telemetry.Scopes;

internal sealed class AppendScope(Activity activity) : IDisposable
{
    private bool _disposed;

    public const string ActivityName = "Alberto.Append";

    public AppendScope WithEvents(IEventToPersist[] events)
    {
        activity.DisplayName = $"Append events";
        foreach (var evt in events)
        {
            Activity.Current?.AddEvent(
                new ActivityEvent(
                    evt.EventType.Id,
                    tags: new ActivityTagsCollection
                    {
                        {
                            Tags.EventId, evt.Id.ToString()
                        },
                        {
                            Tags.EventType, evt.EventType.Id
                        },
                        {
                            Tags.EventTags, string.Join(",", evt.Tags.Select(x => x.FullIdentifier))
                        }
                    }));
        }

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