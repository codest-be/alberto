using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Unit tests for the batch consume middleware pipeline.
/// </summary>
public sealed class BatchMiddlewareTests
{
    private static BatchConsumeContext MakeContext(
        IReadOnlyList<IEventEnvelope>? envelopes = null,
        CancellationToken ct = default)
    {
        var batch = envelopes ?? [MakeEnvelope(1)];
        return new BatchConsumeContext
        {
            ProcessorId = "test-processor",
            ModuleKey = "test-module",
            Envelopes = batch,
            IsRebuild = false,
            CancellationToken = ct,
        };
    }

    private static IEventEnvelope MakeEnvelope(long position) =>
        new EventEnvelope
        {
            Id = Guid.NewGuid(),
            GlobalPosition = position,
            EventType = new EventType("test-event"),
            Tags = [],
            EventData = "{}",
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow,
        };

    [Fact]
    public async Task BatchMiddlewares_ExecuteInRegistrationOrder()
    {
        var order = new List<int>();

        BatchConsumeMiddleware first = async (_, next) =>
        {
            order.Add(1);
            await next();
            order.Add(4);
        };
        BatchConsumeMiddleware second = async (_, next) =>
        {
            order.Add(2);
            await next();
            order.Add(3);
        };

        await BatchMiddlewareRunner.RunAsync(
            MakeContext(ct: TestContext.Current.CancellationToken),
            [first, second],
            () =>
            {
                order.Add(99);
                return Task.CompletedTask;
            });

        Assert.Equal([1, 2, 99, 3, 4], order);
    }

    [Fact]
    public async Task RetryAndDeadLetter_DeadLettersSingleEventBatch()
    {
        var deadLetterStore = new InMemoryDeadLetterStore();
        var middleware = BatchConsumeMiddlewares.RetryAndDeadLetter(
            new ErrorPolicy
            {
                MaxRetries = 0,
                ErrorClassifier = new AlwaysPermanentClassifier(),
            },
            deadLetterStore);

        var context = MakeContext(ct: TestContext.Current.CancellationToken);

        await BatchMiddlewareRunner.RunAsync(
            context,
            [middleware],
            () => throw new InvalidOperationException("always fails"));

        Assert.Equal(1, context.DeadLetteredCount);
        Assert.Equal(1, context.Attempt);
        Assert.NotNull(context.LastError);
        var entries = await deadLetterStore.GetAsync("test-processor", ct: TestContext.Current.CancellationToken);
        Assert.Single(entries);
    }

    [Fact]
    public async Task RetryAndDeadLetter_RethrowsFailedMultiEventBatch()
    {
        var middleware = BatchConsumeMiddlewares.RetryAndDeadLetter(
            new ErrorPolicy
            {
                MaxRetries = 0,
                ErrorClassifier = new AlwaysPermanentClassifier(),
            });

        var context = MakeContext(
            [MakeEnvelope(1), MakeEnvelope(2)],
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BatchMiddlewareRunner.RunAsync(
                context,
                [middleware],
                () => throw new InvalidOperationException("always fails")));

        Assert.Equal(0, context.DeadLetteredCount);
        Assert.Equal(1, context.Attempt);
        Assert.NotNull(context.LastError);
    }

    private sealed class AlwaysPermanentClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception) => ErrorClassification.Permanent;
    }
}
