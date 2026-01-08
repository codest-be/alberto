using System.Diagnostics;
using Alberto.Dcb.Subscriptions.Pipeline;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Consume filter that adds OpenTelemetry instrumentation.
/// Records traces, metrics, and timing for event processing.
/// </summary>
public sealed class TelemetryConsumeFilter : IConsumeFilter
{
    /// <inheritdoc />
    public async Task OnConsumingAsync(
        IReadOnlyList<IEventEnvelope> events,
        ConsumeContext context,
        Func<Task> next,
        CancellationToken ct = default)
    {
        using var activity = AlbertoTelemetry.Source.StartActivity(
            AlbertoTelemetry.ConsumeActivityName,
            ActivityKind.Consumer);

        activity?.SetTag("processor.id", context.ProcessorId);
        activity?.SetTag("module.key", context.ModuleKey);
        activity?.SetTag("events.count", events.Count);

        if (events.Count > 0)
        {
            activity?.SetTag("events.first_position", events[0].GlobalPosition);
            activity?.SetTag("events.last_position", events[^1].GlobalPosition);
        }

        AlbertoTelemetry.BatchSize.Record(events.Count,
            new KeyValuePair<string, object?>("processor", context.ProcessorId));

        var sw = Stopwatch.StartNew();
        try
        {
            await next();

            activity?.SetStatus(ActivityStatusCode.Ok);
            AlbertoTelemetry.EventsProcessed.Add(events.Count,
                new KeyValuePair<string, object?>("processor", context.ProcessorId),
                new KeyValuePair<string, object?>("module", context.ModuleKey));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddTag("exception.type", ex.GetType().FullName);
            activity?.AddTag("exception.message", ex.Message);
            activity?.AddTag("exception.stacktrace", ex.StackTrace);

            AlbertoTelemetry.ProcessingErrors.Add(1,
                new KeyValuePair<string, object?>("processor", context.ProcessorId),
                new KeyValuePair<string, object?>("exception.type", ex.GetType().Name));

            throw;
        }
        finally
        {
            AlbertoTelemetry.ProcessingDuration.Record(sw.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("processor", context.ProcessorId));
        }
    }
}
