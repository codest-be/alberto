using System.Reflection;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Targeted tests written to kill specific Stryker mutation survivors in
/// <c>ControlLoopRegistration</c>, <c>ControlLoopAssembler</c>, <c>ProcessorHealthCheck</c> and
/// <c>ProcessorHealthState</c>. Each test's doc comment names the survivor it targets.
/// </summary>
public class ProcessorHealthWiringMutationTests
{
    #region ProcessorHealthState.cs:25 — NotYetReported

    /// <summary>Kills ProcessorHealthState.cs:25 (IsFaulted: false → true in NotYetReported).</summary>
    [Fact]
    public void Unknown_processor_snapshot_is_not_faulted_and_has_zero_lag()
    {
        var state = new ProcessorHealthState();

        var snapshot = state.Get("never-reported");

        snapshot.IsFaulted.Should().BeFalse();
        snapshot.Lag.Should().Be(0);
        snapshot.HasReported.Should().BeFalse();
    }

    #endregion

    #region ProcessorHealthCheck.cs:67 — lag boundary

    /// <summary>Kills ProcessorHealthCheck.cs:67 (snapshot.Lag > threshold → >=): lag exactly at
    /// the threshold must still be Healthy, not Degraded.</summary>
    [Fact]
    public async Task Lag_exactly_at_the_degraded_threshold_is_healthy()
    {
        var time = new FakeTimeProvider();
        var state = new ProcessorHealthState();
        state.Report("orders-proj", isFaulted: false, lag: 100, time.GetUtcNow());

        var result = await Check(state, time, degradedLagThreshold: 100);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["orders-proj"].Should().Be("healthy");
    }

    private static Task<HealthCheckResult> Check(
        ProcessorHealthState state, TimeProvider time, long degradedLagThreshold = long.MaxValue) =>
        new ProcessorHealthCheck("orders", state, TimeSpan.FromSeconds(5), degradedLagThreshold, time)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    #endregion

    #region ControlLoopRegistration.cs:83,113 — staleness threshold = Max(5s, PollingInterval * 5)

    /// <summary>
    /// Kills ControlLoopRegistration.cs:83 (PollingInterval * 5 → / 5). With a 10s polling
    /// interval the staleness threshold must be 50s (the right side of Max wins), so a
    /// 20s-old heartbeat is still Healthy. Under the `/ 5` mutant the threshold would be 2s
    /// and this heartbeat would already read Unhealthy.
    /// </summary>
    [Fact]
    public async Task Registered_health_check_uses_five_times_polling_interval_as_staleness_threshold()
    {
        var fakeTime = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fakeTime);
        services.AddAlberto("orders", module => module
            .WithInMemory()
            .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromSeconds(10) }));

        await using var sp = services.BuildServiceProvider();

        var state = sp.GetRequiredKeyedService<ProcessorHealthState>("orders");
        state.Report("proc", isFaulted: false, lag: 0, fakeTime.GetUtcNow());

        var registration = GetRegistration(sp, "alberto-processors-orders");
        var check = registration.Factory(sp);

        // 20s elapsed: well past a naive "/5" threshold of 2s, well under the real 50s one.
        fakeTime.Advance(TimeSpan.FromSeconds(20));
        var stillHealthy = await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration }, TestContext.Current.CancellationToken);
        stillHealthy.Status.Should().Be(HealthStatus.Healthy);

        // Cross the real 50s threshold (20s + 31s = 51s).
        fakeTime.Advance(TimeSpan.FromSeconds(31));
        var nowUnhealthy = await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration }, TestContext.Current.CancellationToken);
        nowUnhealthy.Status.Should().Be(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// Documents the 5-second floor from ControlLoopRegistration.cs:82-83,113: a very fast
    /// polling interval (1s) still gets a 5s staleness floor, not 5s * 1 = 5s coincidentally
    /// equal here (5 * 1s = 5s = the floor), so a 4s-old heartbeat is Healthy either way.
    /// NOTE: because both sides of Max evaluate to 5s at this input, this does not
    /// independently kill the `>` → `>=` mutant on line 113 (the branch taken differs but the
    /// selected value does not) — see the "skipped" list in the task summary.
    /// </summary>
    [Fact]
    public async Task Registered_health_check_floors_staleness_threshold_at_five_seconds()
    {
        var fakeTime = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fakeTime);
        services.AddAlberto("orders", module => module
            .WithInMemory()
            .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromSeconds(1) }));

        await using var sp = services.BuildServiceProvider();

        var state = sp.GetRequiredKeyedService<ProcessorHealthState>("orders");
        state.Report("proc", isFaulted: false, lag: 0, fakeTime.GetUtcNow());

        var registration = GetRegistration(sp, "alberto-processors-orders");
        var check = registration.Factory(sp);

        fakeTime.Advance(TimeSpan.FromSeconds(4));
        var result = await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration }, TestContext.Current.CancellationToken);
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    private static HealthCheckRegistration GetRegistration(IServiceProvider sp, string name) =>
        sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Single(r => r.Name == name);

    #endregion

    #region ControlLoopRegistration.cs:29,32 — keyed IErrorClassifier / keyed backend fallback

    /// <summary>
    /// Kills ControlLoopRegistration.cs:29 (the keyed-classifier lookup collapsed to always
    /// return DefaultErrorClassifier.Instance). A custom IErrorClassifier registered keyed
    /// under the module key must be the one the control loop's retry middleware consults when
    /// a processor faults.
    /// </summary>
    [Fact]
    public async Task Control_loop_uses_keyed_error_classifier_when_one_is_registered()
    {
        var spy = new SpyErrorClassifier();
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithInMemory()
            .ReactTo<MutationTestEvent>(
                _ => (_, _) => throw new InvalidOperationException("boom"),
                "spy-classifier-proc"));
        services.AddKeyedSingleton<IErrorClassifier>("orders", spy);

        await using var sp = services.BuildServiceProvider();

        var backend = sp.GetRequiredKeyedService<IEventStoreBackend>("orders");
        await backend.AppendAsync([Event()], cancellationToken: TestContext.Current.CancellationToken);

        var hostedServices = sp.GetServices<IHostedService>().ToList();
        foreach (var service in hostedServices)
            await service.StartAsync(TestContext.Current.CancellationToken);

        await WaitForAsync(() => spy.ClassifyCallCount > 0, TestContext.Current.CancellationToken);

        for (var i = hostedServices.Count - 1; i >= 0; i--)
            await hostedServices[i].StopAsync(CancellationToken.None);

        spy.ClassifyCallCount.Should().BeGreaterThan(0,
            "the keyed IErrorClassifier registered for the module must be consulted on fault");
    }

    /// <summary>
    /// Kills ControlLoopRegistration.cs:32 (the "{moduleKey}:consumer" keyed backend lookup
    /// dropped in favour of the plain moduleKey backend). When a ":consumer" backend is
    /// registered, EventStoreHead must read from it, not from the module's primary backend.
    /// </summary>
    [Fact]
    public async Task Event_store_head_reads_from_the_keyed_consumer_backend_when_present()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());

        var consumerBackend = new InMemoryEventStoreBackend();
        services.AddKeyedSingleton<IEventStoreBackend>("orders:consumer", consumerBackend);

        await using var sp = services.BuildServiceProvider();

        // Event only exists on the ":consumer" backend, never on the module's own "orders" one.
        await consumerBackend.AppendAsync([Event()], cancellationToken: TestContext.Current.CancellationToken);

        var head = sp.GetRequiredKeyedService<EventStoreHead>("orders");
        await head.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => head.Current >= 1, TestContext.Current.CancellationToken);
        await head.StopAsync(CancellationToken.None);

        head.Current.Should().Be(1);
    }

    private sealed class SpyErrorClassifier : IErrorClassifier
    {
        private int _classifyCallCount;
        public int ClassifyCallCount => Volatile.Read(ref _classifyCallCount);

        public ErrorClassification Classify(Exception exception)
        {
            Interlocked.Increment(ref _classifyCallCount);
            // Permanent: dead-letters immediately, keeping the test fast and avoiding a
            // dependency on the module's retry-count configuration.
            return ErrorClassification.Permanent;
        }
    }

    #endregion

    #region ControlLoopAssembler.cs:79 — diBatchMiddlewares dropped from the batch chain

    /// <summary>
    /// Kills ControlLoopAssembler.cs:79 (the batch-middleware list initializer stops copying
    /// the DI-supplied batch middlewares). A batch middleware passed to the assembler's
    /// constructor must run when the assembled loop dispatches a batch.
    /// </summary>
    [Fact]
    public async Task Assembler_includes_di_batch_middlewares_in_the_assembled_batch_chain()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));
        var batchMiddleware = new RecordingBatchMiddleware();

        var assembler = new ControlLoopAssembler(
            diMiddlewares: [],
            diBatchMiddlewares: [batchMiddleware.ToMiddleware()],
            retryOptions: new RetryOptions { MaxRetries = 0 },
            classifier: DefaultErrorClassifier.Instance,
            deadLetterStore: null);

        var processor = new SimpleBatchProcessor("batch-proc");
        var loop = assembler.Create(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            moduleKey: "test",
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Required });

        await backend.AppendAsync([Event()], cancellationToken: TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await WaitForAsync(() => batchMiddleware.SeenBatchCount > 0, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        batchMiddleware.SeenBatchCount.Should().BeGreaterThan(0,
            "the DI-supplied batch middleware must be part of the assembled batch chain");
    }

    private sealed class RecordingBatchMiddleware
    {
        private int _seenBatchCount;
        public int SeenBatchCount => Volatile.Read(ref _seenBatchCount);

        public BatchConsumeMiddleware ToMiddleware() => (_, next) =>
        {
            Interlocked.Increment(ref _seenBatchCount);
            return next();
        };
    }

    private sealed class SimpleBatchProcessor(string processorId) : IBatchableProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { "processor-health-wiring-mutation-event" };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    #endregion

    #region ControlLoopAssembler.cs:131,134,135 — fence violation subscription/routing/cancel

    /// <summary>Kills ControlLoopAssembler.cs:131 (fencable.SubscribeFenceViolation(...) removed):
    /// the assembler must subscribe a handler when the checkpoint store is fencable.</summary>
    [Fact]
    public void Assembler_subscribes_a_fence_violation_handler_when_store_is_fencable()
    {
        var (assembler, fencableStore) = CreateAssemblerAndFencableStore();
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));

        assembler.Create(
            new SimpleBatchProcessor("fence-proc"), head, backend, fencableStore,
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 100, moduleKey: "test",
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled });

        fencableStore.Handler.Should().NotBeNull(
            "the assembler must subscribe a fence-violation handler on a fencable checkpoint store");
    }

    /// <summary>Kills ControlLoopAssembler.cs:134 (violatingProcessorId == loop.ProcessorId → !=):
    /// a violation reported for a DIFFERENT processor id must not cancel this loop.</summary>
    [Fact]
    public async Task Fence_violation_for_a_different_processor_does_not_cancel_this_loop()
    {
        var (assembler, fencableStore) = CreateAssemblerAndFencableStore();
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));

        var loop = assembler.Create(
            new SimpleBatchProcessor("fence-proc"), head, backend, fencableStore,
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 100, moduleKey: "test",
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled });

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        fencableStore.Handler.Should().NotBeNull();
        fencableStore.Handler!.Invoke("some-other-processor");

        GetLoopCts(loop)!.IsCancellationRequested.Should().BeFalse(
            "a fence violation for a different processor id must not cancel this loop");

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);
    }

    /// <summary>Kills ControlLoopAssembler.cs:135 (loop.Cancel() removed): a violation reported
    /// for THIS processor's id must cancel the loop's internal token.</summary>
    [Fact]
    public async Task Fence_violation_for_this_processor_cancels_the_loop()
    {
        var (assembler, fencableStore) = CreateAssemblerAndFencableStore();
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(20));

        var loop = assembler.Create(
            new SimpleBatchProcessor("fence-proc"), head, backend, fencableStore,
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 100, moduleKey: "test",
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled });

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        fencableStore.Handler.Should().NotBeNull();
        fencableStore.Handler!.Invoke(loop.ProcessorId);

        GetLoopCts(loop)!.IsCancellationRequested.Should().BeTrue(
            "a fence violation for this loop's own processor id must cancel it");

        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);
    }

    private static (ControlLoopAssembler Assembler, FakeFencableCheckpointStore Store) CreateAssemblerAndFencableStore()
    {
        var assembler = new ControlLoopAssembler(
            diMiddlewares: [],
            diBatchMiddlewares: [],
            retryOptions: new RetryOptions { MaxRetries = 0 },
            classifier: DefaultErrorClassifier.Instance,
            deadLetterStore: null);
        return (assembler, new FakeFencableCheckpointStore());
    }

    /// <summary>
    /// Reads ControlLoop's private `_cts` field via reflection. There is no public surface for
    /// "was this loop's internal token cancelled" — reflection is the only way to observe the
    /// assembler's fence-violation wiring deterministically and without wall-clock races.
    /// </summary>
    private static CancellationTokenSource? GetLoopCts(ControlLoop loop) =>
        (CancellationTokenSource?)typeof(ControlLoop)
            .GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(loop);

    private sealed class FakeFencableCheckpointStore : ICheckpointStore, IFencableCheckpointStore
    {
        private readonly InMemoryCheckpointStore _inner = new();

        public Action<string>? Handler { get; private set; }

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default) =>
            _inner.GetAsync(processorId, ct);

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default) =>
            _inner.SaveAsync(processorId, position, ct);

        public Task ResetAsync(string processorId, CancellationToken ct = default) =>
            _inner.ResetAsync(processorId, ct);

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default) =>
            _inner.RewindAsync(processorId, position, ct);

        public void SetFencingContext(FencingContext ctx)
        {
            // Not exercised by these tests — only the fence-violation subscription is.
        }

        public void SubscribeFenceViolation(Action<string> handler) => Handler = handler;
    }

    #endregion

    #region Helpers

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within 10s.");
            await Task.Delay(10, ct);
        }
    }

    private static EventToPersist Event() => new()
    {
        EventType = new EventType("processor-health-wiring-mutation-event"),
        Tags = [],
        EventData = System.Text.Json.JsonSerializer.Serialize(new MutationTestEvent("x")),
    };

    [EventType("processor-health-wiring-mutation-event")]
    public record MutationTestEvent(string Value) : IEvent;

    #endregion
}
