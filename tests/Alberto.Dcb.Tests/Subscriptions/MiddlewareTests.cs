using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Unit tests for the ConsumeMiddleware / MiddlewareRunner pipeline.
/// No database required — pure in-memory logic.
/// </summary>
public sealed class MiddlewareTests
{
    private static readonly DateTime EventCreatedAt =
        new(2026, 7, 26, 12, 34, 56, DateTimeKind.Utc);

    #region Helpers

    private static ConsumeEventContext MakeContext(CancellationToken ct = default)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = "tenant-a",
            GlobalPosition = 1,
            EventType = new EventType("test-event"),
            Tags = [new EventTag("order", "123")],
            EventData = "{}",
            Metadata = new Dictionary<string, string> { ["correlation-id"] = "corr-1" },
            CreatedAt = EventCreatedAt
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
            new RetryOptions { MaxRetries = 3 },
            DefaultErrorClassifier.Instance,
            deadLetterStore: null);

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
        var retry = new RetryOptions
        {
            MaxRetries = 3,
            RetryDelay = TimeSpan.Zero,
        };

        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(retry, new AlwaysTransientClassifier(), deadLetterStore: null);
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
        var retry = new RetryOptions
        {
            MaxRetries = 2,
            RetryDelay = TimeSpan.Zero,
        };

        var deadLetterStore = new InMemoryDeadLetterStore();
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(retry, new AlwaysTransientClassifier(), deadLetterStore);
        var context = MakeContext(TestContext.Current.CancellationToken);

        await MiddlewareRunner.RunAsync(context, [middleware], () =>
            throw new Exception("always fails"));

        Assert.True(context.DeadLettered);
        Assert.NotNull(context.LastError);
        Assert.Equal(3, context.Attempt); // 1 initial + 2 retries
        Assert.Single(deadLetterStore.Entries);
        var entry = deadLetterStore.Entries[0];
        Assert.Equal(context.ProcessorId, entry.ProcessorId);
        Assert.Equal("tenant-a", entry.TenantId);
        Assert.Equal(["order:123"], entry.Tags);
        Assert.Equal("corr-1", entry.Metadata!["correlation-id"]);
        Assert.Equal(EventCreatedAt, entry.CreatedAt);
    }

    #endregion

    #region RetryAndDeadLetter — permanent errors

    [Fact]
    public async Task RetryAndDeadLetter_PermanentError_DeadLettersImmediatelyWithoutRetry()
    {
        var callCount = 0;
        var retry = new RetryOptions
        {
            MaxRetries = 5,
            DeadLetterOnMaxRetries = true,
        };

        var deadLetterStore = new InMemoryDeadLetterStore();
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(retry, new AlwaysPermanentClassifier(), deadLetterStore);
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
        var retry = new RetryOptions
        {
            MaxRetries = 3,
            DeadLetterOnMaxRetries = true,
        };

        // No dead letter store provided
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(retry, new AlwaysPermanentClassifier(), deadLetterStore: null);
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

        var retry = new RetryOptions { MaxRetries = 3, RetryDelay = TimeSpan.Zero };
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(retry, DefaultErrorClassifier.Instance, deadLetterStore: null);
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
        var retry = new RetryOptions
        {
            MaxRetries = 1,
            RetryDelay = TimeSpan.Zero,
            DeadLetterOnMaxRetries = false,
        };

        var deadLetterStore = new InMemoryDeadLetterStore();
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(retry, new AlwaysTransientClassifier(), deadLetterStore);
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

        public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(string processorId, string? tenantId = null, int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeadLetterEntry>>(Entries.Where(e => e.ProcessorId == processorId).ToList());

        public Task<int> CountAsync(string processorId, CancellationToken ct = default)
            => Task.FromResult(Entries.Count(e => e.ProcessorId == processorId));

        public Task<bool> CompleteRetryAsync(DeadLetterClaim claim, CancellationToken ct = default)
        {
            var removed = Entries.RemoveAll(e => e.Id == claim.Entry.Id && e.ClaimId == claim.Token);
            return Task.FromResult(removed == 1);
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

        public Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(
            string processorId,
            int batchSize,
            TimeSpan leaseDuration,
            string claimedBy,
            CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            var lease = now + leaseDuration;
            var claimed = new List<DeadLetterClaim>();
            for (var index = 0; index < Entries.Count && claimed.Count < batchSize; index++)
            {
                var existing = Entries[index];
                if (existing.ProcessorId != processorId || !existing.RetryRequested)
                    continue;
                if (existing.ClaimExpiresAt is { } exp && exp >= now)
                    continue;

                var token = Guid.NewGuid();
                var updated = existing with
                {
                    ClaimedAt = now,
                    ClaimExpiresAt = lease,
                    ClaimedBy = claimedBy,
                    ClaimId = token,
                };
                Entries[index] = updated;
                claimed.Add(new DeadLetterClaim(updated, token, lease));
            }
            return Task.FromResult<IReadOnlyList<DeadLetterClaim>>(claimed);
        }

        public Task<bool> AbandonRetryAsync(DeadLetterClaim claim, CancellationToken ct = default)
        {
            for (var index = 0; index < Entries.Count; index++)
            {
                if (Entries[index].Id == claim.Entry.Id && Entries[index].ClaimId == claim.Token)
                {
                    Entries[index] = Entries[index] with
                    {
                        RetryRequested = false,
                        ClaimedAt = null,
                        ClaimExpiresAt = null,
                        ClaimedBy = null,
                        ClaimId = null,
                    };
                    return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }
    }

    #endregion
}
