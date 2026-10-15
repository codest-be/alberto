using System.Diagnostics;
using Alberto.Subscriptions;
using Alberto.Telemetry;
using Xunit;

namespace Alberto.Tests.Telemetry;

/// <summary>
/// The span shape a consumer of the traces can rely on: messaging.* semantic conventions, alberto.*
/// for Alberto's own attributes, and failures that name the exception type but never its message.
/// </summary>
[Collection(OtelTracingCollection.Name)]
public sealed class SemconvSpanTests
{
    private static async Task<Activity> ConsumeAsync(Func<Task> next)
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Alberto",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var context = new ConsumeEventContext
        {
            ProcessorId = "semconv-proc",
            ModuleKey = "orders#eu",
            Envelope = new EventEnvelope
            {
                Id = Guid.NewGuid(), GlobalPosition = 3, EventType = new EventType("e"), Tags = [],
                EventData = "{}", Metadata = new Dictionary<string, string>(), CreatedAt = DateTime.UtcNow,
            },
            IsRebuild = false,
        };
        try { await TelemetryConsumeMiddleware.Create()(context, next); } catch (InvalidOperationException) { }
        lock (stopped) return stopped.Single(a => a.GetTagItem("alberto.processor.id") as string == "semconv-proc");
    }

    [Fact]
    public async Task Consume_span_follows_messaging_conventions_and_stays_unset_on_success()
    {
        var span = await ConsumeAsync(() => Task.CompletedTask);

        Assert.Equal("process semconv-proc", span.DisplayName);
        Assert.Equal("alberto", span.GetTagItem("messaging.system"));
        Assert.Equal("process", span.GetTagItem("messaging.operation.type"));
        Assert.Equal("semconv-proc", span.GetTagItem("messaging.consumer.group.name"));
        Assert.Equal("orders", span.GetTagItem("messaging.destination.name"));
        Assert.Equal("eu", span.GetTagItem("alberto.shard"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task Failed_consume_records_the_exception_type_and_never_its_message()
    {
        const string secret = "42P01: relation \"alberto_events\" does not exist";

        var span = await ConsumeAsync(() => throw new InvalidOperationException(secret));

        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
        var exceptionEvent = Assert.Single(span.Events, e => e.Name == "exception");
        Assert.DoesNotContain(exceptionEvent.Tags, t => t.Key == "exception.message" || t.Key == "exception.stacktrace");
        Assert.DoesNotContain(span.TagObjects, t => t.Value?.ToString()?.Contains(secret) == true);
    }
}
