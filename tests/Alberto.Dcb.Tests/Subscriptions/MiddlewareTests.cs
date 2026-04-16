using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Unit tests for the ConsumeMiddleware / MiddlewareRunner pipeline.
/// No database required — pure in-memory logic.
/// </summary>
public sealed class MiddlewareTests
{
    #region Helpers

    private static ConsumeEventContext MakeContext(CancellationToken ct = default)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid(),
            GlobalPosition = 1,
            EventType = new EventType("test-event"),
            Tags = [],
            EventData = "{}",
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow
        };
        return new ConsumeEventContext
        {
            ProcessorId = "test-processor",
            ModuleKey = "test-module",
            Envelope = envelope,
            IsRebuild = false,
            CancellationToken = ct
        };
    }

    #endregion

    #region Middleware ordering

    [Fact]
    public async Task Middlewares_ExecuteInRegistrationOrder()
    {
        var order = new List<int>();

        ConsumeMiddleware first = async (ctx, next) =>
        {
            order.Add(1);
            await next();
            order.Add(4);
        };
        ConsumeMiddleware second = async (ctx, next) =>
        {
            order.Add(2);
            await next();
            order.Add(3);
        };

        var middlewares = new List<ConsumeMiddleware> { first, second };
        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, middlewares, () =>
        {
            order.Add(99);
            return Task.CompletedTask;
        });

        Assert.Equal([1, 2, 99, 3, 4], order);
    }

    [Fact]
    public async Task NoMiddlewares_InvokesTerminalDirectly()
    {
        var terminalCalled = false;
        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, [], () =>
        {
            terminalCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(terminalCalled);
    }

    [Fact]
    public async Task SingleMiddleware_WrapsTerminal()
    {
        var log = new List<string>();

        ConsumeMiddleware middleware = async (ctx, next) =>
        {
            log.Add("before");
            await next();
            log.Add("after");
        };

        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, [middleware], () =>
        {
            log.Add("terminal");
            return Task.CompletedTask;
        });

        Assert.Equal(["before", "terminal", "after"], log);
    }

    #endregion

    #region Short-circuiting

    [Fact]
    public async Task Middleware_ThatDoesNotCallNext_ShortCircuitsChain()
    {
        var terminalCalled = false;
        var secondMiddlewareCalled = false;

        ConsumeMiddleware shortCircuit = (ctx, next) =>
        {
            // Intentionally does NOT call next
            return Task.CompletedTask;
        };
        ConsumeMiddleware second = async (ctx, next) =>
        {
            secondMiddlewareCalled = true;
            await next();
        };

        var context = MakeContext(TestContext.Current.CancellationToken);
        await MiddlewareRunner.RunAsync(context, [shortCircuit, second], () =>
        {
            terminalCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(terminalCalled);
        Assert.False(secondMiddlewareCalled);
    }

    #endregion

    #region RetryAndDeadLetter — successful processing

    [Fact]
    public async Task RetryAndDeadLetter_SuccessOnFirstAttempt_DoesNotDeadLetter()
    {
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            new ErrorPolicy { MaxRetries = 3 });

        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, [middleware], () => Task.CompletedTask);

        Assert.False(context.DeadLettered);
        Assert.Equal(1, context.Attempt);
        Assert.Null(context.LastError);
    }

    #endregion

    #region RetryAndDeadLetter — transient errors

    [Fact]
    public async Task RetryAndDeadLetter_RetriesOnTransientError_SucceedsOnSecondAttempt()
    {
        var callCount = 0;
        var policy = new ErrorPolicy
        {
            MaxRetries = 3,
            RetryDelay = TimeSpan.Zero,
            ErrorClassifier = new AlwaysTransientClassifier()
        };

        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(policy);
        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, [middleware], () =>
        {
            callCount++;
            if (callCount < 2)
                throw new InvalidOperationException("transient");
            return Task.CompletedTask;
        });

        Assert.Equal(2, callCount);
        Assert.False(context.DeadLettered);
        Assert.Null(context.LastError);
    }

    [Fact]
    public async Task RetryAndDeadLetter_ExhaustsRetries_DeadLetters()
    {
        var policy = new ErrorPolicy
        {
            MaxRetries = 2,
            RetryDelay = TimeSpan.Zero,
            ErrorClassifier = new AlwaysTransientClassifier()
        };

        var deadLetterStore = new InMemoryDeadLetterStore();
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(policy, deadLetterStore);
        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, [middleware], () =>
            throw new Exception("always fails"));

        Assert.True(context.DeadLettered);
        Assert.NotNull(context.LastError);
        Assert.Equal(3, context.Attempt); // 1 initial + 2 retries
        Assert.Single(deadLetterStore.Entries);
        Assert.Equal(context.ProcessorId, deadLetterStore.Entries[0].ProcessorId);
    }

    #endregion

    #region RetryAndDeadLetter — permanent errors

    [Fact]
    public async Task RetryAndDeadLetter_PermanentError_DeadLettersImmediatelyWithoutRetry()
    {
        var callCount = 0;
        var policy = new ErrorPolicy
        {
            MaxRetries = 5,
            DeadLetterOnMaxRetries = true,
            ErrorClassifier = new AlwaysPermanentClassifier()
        };

        var deadLetterStore = new InMemoryDeadLetterStore();
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(policy, deadLetterStore);
        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, [middleware], () =>
        {
            callCount++;
            throw new ArgumentException("permanent error");
        });

        // Should only be called once — no retries for permanent errors
        Assert.Equal(1, callCount);
        Assert.True(context.DeadLettered);
        Assert.Single(deadLetterStore.Entries);
    }

    [Fact]
    public async Task RetryAndDeadLetter_PermanentError_NoDeadLetterStore_StillMarksDeadLettered()
    {
        var policy = new ErrorPolicy
        {
            MaxRetries = 3,
            DeadLetterOnMaxRetries = true,
            ErrorClassifier = new AlwaysPermanentClassifier()
        };

        // No dead letter store provided
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(policy, deadLetterStore: null);
        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, [middleware], () =>
            throw new ArgumentException("permanent"));

        Assert.True(context.DeadLettered);
    }

    #endregion

    #region RetryAndDeadLetter — cancellation

    [Fact]
    public async Task RetryAndDeadLetter_CancellationRequested_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var policy = new ErrorPolicy { MaxRetries = 3, RetryDelay = TimeSpan.Zero };
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(policy);
        var context = MakeContext(cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            MiddlewareRunner.RunAsync(context, [middleware], () =>
                throw new OperationCanceledException(cts.Token)));
    }

    #endregion

    #region RetryAndDeadLetter — DeadLetterOnMaxRetries=false

    [Fact]
    public async Task RetryAndDeadLetter_DeadLetterDisabled_SkipsDeadLetterStore()
    {
        var policy = new ErrorPolicy
        {
            MaxRetries = 1,
            RetryDelay = TimeSpan.Zero,
            DeadLetterOnMaxRetries = false,
            ErrorClassifier = new AlwaysTransientClassifier()
        };

        var deadLetterStore = new InMemoryDeadLetterStore();
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(policy, deadLetterStore);
        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, [middleware], () =>
            throw new Exception("failure"));

        // Still marked as dead-lettered internally but nothing stored
        Assert.True(context.DeadLettered);
        Assert.Empty(deadLetterStore.Entries);
    }

    #endregion

    #region Context mutation through chain

    [Fact]
    public async Task Context_IsMutatedByMiddleware_AndVisibleToNext()
    {
        ConsumeMiddleware setter = async (ctx, next) =>
        {
            ctx.Attempt = 42;
            await next();
        };

        int attemptSeenInTerminal = 0;
        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, [setter], () =>
        {
            attemptSeenInTerminal = context.Attempt;
            return Task.CompletedTask;
        });

        Assert.Equal(42, attemptSeenInTerminal);
    }

    #endregion

    #region Test helpers

    private sealed class AlwaysTransientClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception) => ErrorClassification.Transient;
    }

    private sealed class AlwaysPermanentClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception) => ErrorClassification.Permanent;
    }

    private sealed class InMemoryDeadLetterStore : IDeadLetterStore
    {
        public List<DeadLetterEntry> Entries { get; } = [];

        public Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(string processorId, int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeadLetterEntry>>(Entries.Where(e => e.ProcessorId == processorId).ToList());

        public Task<int> CountAsync(string processorId, CancellationToken ct = default)
            => Task.FromResult(Entries.Count(e => e.ProcessorId == processorId));

        public Task RemoveAsync(Guid id, CancellationToken ct = default)
        {
            Entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string processorId, CancellationToken ct = default)
        {
            Entries.RemoveAll(e => e.ProcessorId == processorId);
            return Task.CompletedTask;
        }

        public Task MarkForRetryAsync(string processorId, CancellationToken ct = default)
        {
            for (var index = 0; index < Entries.Count; index++)
            {
                if (Entries[index].ProcessorId == processorId)
                    Entries[index] = Entries[index] with { RetryRequested = true };
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeadLetterEntry>> GetRetryRequestedWithLockAsync(
            string processorId,
            int batchSize = 10,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeadLetterEntry>>(Entries
                .Where(e => e.ProcessorId == processorId && e.RetryRequested)
                .Take(batchSize)
                .ToList());
    }

    #endregion
}
