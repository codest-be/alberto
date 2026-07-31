using System.Diagnostics;
using Alberto.Append;
using Alberto.Telemetry;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Telemetry;

/// <summary>
/// Pins what an append span is allowed to carry off the machine.
/// </summary>
/// <remarks>
/// <para>
/// Two separate leaks are covered. Event tag <em>values</em> are domain identifiers — the whole
/// point of a DCB tag is to name the entity a consistency boundary is drawn around — so they are
/// withheld unless the operator asks for them. Exception detail is recorded as an exception
/// <em>event</em> rather than as span attributes, because messages carry whatever the thrower put
/// in them (Npgsql's include the failing SQL) and collectors already know how to filter the
/// conventional event.
/// </para>
/// <para>
/// The listener is process-wide and other suites emit Alberto activities concurrently, so every
/// assertion here locates its own span by the event id it appended rather than taking whichever
/// span happened to stop first.
/// </para>
/// </remarks>
public sealed class AppendSpanPrivacyTests
{
    private static readonly EventType OrderPlaced = new("order-placed");

    [Fact]
    public async Task By_default_an_append_span_records_tag_concepts_but_not_their_ids()
    {
        var eventId = Guid.CreateVersion7();
        var interceptor = new TelemetryAppendInterceptor();

        var span = await CaptureAppendSpanFor(eventId, () =>
            interceptor.OnAppendingAsync(ContextFor(eventId), _ => Task.FromResult(NoResult)));

        var tags = AppendingEventTag(span, "event.tags");

        tags.Should().Be("order,customer",
            "the concept names identify the consistency boundary without naming the entities");
        tags.Should().NotContain("8f21").And.NotContain("4471",
            "tag ids are domain identifiers and must not leave the process by default");
    }

    [Fact]
    public async Task RecordEventTagValues_opts_in_to_the_full_concept_and_id()
    {
        var eventId = Guid.CreateVersion7();
        var interceptor = new TelemetryAppendInterceptor(recordEventTagValues: true);

        var span = await CaptureAppendSpanFor(eventId, () =>
            interceptor.OnAppendingAsync(ContextFor(eventId), _ => Task.FromResult(NoResult)));

        AppendingEventTag(span, "event.tags").Should().Be("order:8f21,customer:4471");
    }

    [Fact]
    public async Task A_repeated_concept_is_not_repeated_in_the_default_rendering()
    {
        // Two ids of one concept describe one boundary dimension. Emitting "order,order"
        // would grow with the batch while saying nothing the first entry did not.
        var eventId = Guid.CreateVersion7();
        var interceptor = new TelemetryAppendInterceptor();
        var context = ContextFor(eventId, new EventTag("order", "8f21"), new EventTag("order", "9c02"));

        var span = await CaptureAppendSpanFor(eventId, () =>
            interceptor.OnAppendingAsync(context, _ => Task.FromResult(NoResult)));

        AppendingEventTag(span, "event.tags").Should().Be("order");
    }

    [Fact]
    public async Task A_failed_append_records_the_exception_as_an_event_not_as_span_attributes()
    {
        var eventId = Guid.CreateVersion7();
        var interceptor = new TelemetryAppendInterceptor();
        const string secret = "42P01: relation \"alberto_events\" does not exist";

        var span = await CaptureAppendSpanFor(eventId, async () =>
        {
            var act = async () => await interceptor.OnAppendingAsync(
                ContextFor(eventId),
                _ => throw new InvalidOperationException(secret));

            await act.Should().ThrowAsync<InvalidOperationException>();
        });

        span.Should().NotBeNull();

        span!.TagObjects.Select(t => t.Key).Should()
            .NotContain("exception.message").And.NotContain("exception.stacktrace",
                "exception detail as a span attribute survives every collector that only scrubs events");

        var exceptionEvent = span.Events.SingleOrDefault(e => e.Name == "exception");
        exceptionEvent.Name.Should().Be("exception",
            "Activity.AddException emits the OTel-conventional event collectors already handle");
        exceptionEvent.Tags.Should().Contain(t =>
            t.Key == "exception.type" && (string?)t.Value == typeof(InvalidOperationException).FullName);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static readonly IReadOnlyCollection<IEventEnvelope> NoResult = [];

    private static AppendContext ContextFor(Guid eventId, params EventTag[] tags) =>
        new()
        {
            Events =
            [
                new EventToPersist
                {
                    Id = eventId,
                    EventType = OrderPlaced,
                    Tags = tags.Length > 0
                        ? tags
                        : [new EventTag("order", "8f21"), new EventTag("customer", "4471")],
                    EventData = """{"total":10}""",
                }
            ],
        };

    /// <summary>
    /// Runs <paramref name="action"/> and returns the append span that carries
    /// <paramref name="eventId"/>, ignoring spans other suites emit concurrently.
    /// </summary>
    private static async Task<Activity?> CaptureAppendSpanFor(Guid eventId, Func<Task> action)
    {
        var captured = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AlbertoMetrics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == AlbertoMetrics.AppendActivityName)
                    lock (captured) captured.Add(activity);
            },
        };

        ActivitySource.AddActivityListener(listener);

        await action();

        lock (captured)
            return captured.SingleOrDefault(a => a.Events.Any(e =>
                e.Tags.Any(t => t.Key == "event.id" && (string?)t.Value == eventId.ToString())));
    }

    private static string? AppendingEventTag(Activity? span, string tagKey)
    {
        span.Should().NotBeNull("the append span must have been recorded");

        var appending = span!.Events.Single(e => e.Name == "event.appending");
        return (string?)appending.Tags.Single(t => t.Key == tagKey).Value;
    }
}
