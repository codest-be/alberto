using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.Tests.Configuration;

/// <summary>
/// Guards the ReplicaId fallback and the missing-lease-manager startup diagnostic in
/// ControlLoopRegistration.
/// </summary>
public class LeaseFencingRegistrationTests
{
    /// <summary>
    /// Stands in for a backend that supports leases, storing events in memory so a host can
    /// actually start.
    /// </summary>
    /// <remarks>
    /// <see cref="InMemoryBackendDescriptor"/> cannot be used directly: it fails validation with
    /// ALB0024 whenever leases are enabled, which is correct for it — it provides no
    /// <see cref="IProcessorLeaseManager"/> — but it means every lease path is unreachable
    /// through it. Only Postgres satisfies both conditions in production, and reaching for a
    /// container here would make a registration test depend on Docker. This descriptor keeps the
    /// in-memory storage and drops only that one validation, so what is under test is
    /// <see cref="ControlLoopRegistration"/>, not the backend.
    /// </remarks>
    private sealed class LeaseCapableBackend : IAlbertoBackendDescriptor
    {
        private readonly InMemoryBackendDescriptor _storage = new();

        public string Name => "LeaseCapableFake";

        public bool SupportsTenancy => true;

        public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;

        public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => [];

        public void Register(AlbertoModuleContext context) => _storage.Register(context);
    }

    // Minimal IProcessorLeaseManager stub. Records the replicaId received in
    // ReleaseAllLeasesAsync, which LeaseAwareControlLoopGroup calls unconditionally on stop.
    // Works even when no processors are registered (TryAcquireAsync never fires), so tests
    // do not need a dummy processor just to observe what the group was built with.
    private sealed class CapturingLeaseManager : IProcessorLeaseManager
    {
        private string? _capturedStopReplicaId;

        /// <summary>The replicaId passed to ReleaseAllLeasesAsync (set on host stop).</summary>
        public string? CapturedStopReplicaId => _capturedStopReplicaId;

        public TimeSpan LeaseDuration => TimeSpan.FromMinutes(1);

        public Task<IProcessorLease?> TryAcquireAsync(
            string consumerId, string processorId, string replicaId, CancellationToken ct = default) =>
            Task.FromResult<IProcessorLease?>(null); // not the acquisition; capture is on stop

        public Task<IReadOnlyList<string>> RenewLeasesAsync(
            string consumerId, string replicaId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task ReleaseAllLeasesAsync(
            string consumerId, string replicaId, CancellationToken ct = default)
        {
            _capturedStopReplicaId = replicaId;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProcessorLeaseInfo>> GetAllLeasesAsync(
            string consumerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProcessorLeaseInfo>>([]);
    }

    private const string ModuleKey = "orders";

    /// <summary>
    /// Builds a host with leases enabled and the capturing stub registered.
    /// The stub must be registered AFTER AddAlberto so AddKeyedSingleton wins over any
    /// earlier registration — the Postgres backend is the only other registrant, and it is
    /// absent here, so ordering does not actually matter, but it is clearest this way.
    /// </summary>
    private static (IHost Host, CapturingLeaseManager LeaseManager) BuildLeasedHost(string? replicaId)
    {
        var leaseManager = new CapturingLeaseManager();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto(ModuleKey, module => module
            .UseBackend(new LeaseCapableBackend())
            .WithControlLoop(o => o with
            {
                Leases = o.Leases with
                {
                    Enabled = true,
                    ReplicaId = replicaId,
                }
            }));

        // Register the stub under the module key so ControlLoopRegistration.cs resolves it.
        builder.Services.AddKeyedSingleton<IProcessorLeaseManager>(ModuleKey, leaseManager);

        return (builder.Build(), leaseManager);
    }

    [Fact]
    public async Task A_null_ReplicaId_falls_back_to_the_machine_name()
    {
        var (host, leaseManager) = BuildLeasedHost(replicaId: null);
        using (host)
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        leaseManager.CapturedStopReplicaId.Should().Be(Environment.MachineName);
    }

    [Fact]
    public async Task An_empty_string_ReplicaId_falls_back_to_the_machine_name()
    {
        // Empty string passed through the null-coalescing operator (??) would reach claimed_by
        // unchanged, making every misconfigured replica look identical and defeating fencing.
        var (host, leaseManager) = BuildLeasedHost(replicaId: "");
        using (host)
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        leaseManager.CapturedStopReplicaId.Should().Be(Environment.MachineName);
    }

    [Fact]
    public async Task A_whitespace_only_ReplicaId_falls_back_to_the_machine_name()
    {
        // Whitespace is equally unusable as an identity in claimed_by.
        var (host, leaseManager) = BuildLeasedHost(replicaId: "   ");
        using (host)
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        leaseManager.CapturedStopReplicaId.Should().Be(Environment.MachineName);
    }

    [Fact]
    public async Task An_explicitly_set_ReplicaId_is_preserved()
    {
        var (host, leaseManager) = BuildLeasedHost(replicaId: "pod-1");
        using (host)
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        leaseManager.CapturedStopReplicaId.Should().Be("pod-1");
    }

    [Fact]
    public async Task A_lease_capable_backend_with_no_lease_manager_throws_ALB0025_at_startup()
    {
        // A backend whose Validate raises no objection to leases but which registers no
        // IProcessorLeaseManager. Leases would silently never be acquired, renewed or fenced,
        // so registration must say so rather than let the container surface a null service.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto(ModuleKey, module => module
            .UseBackend(new LeaseCapableBackend())
            .WithControlLoop(o => o with
            {
                Leases = o.Leases with { Enabled = true }
            }));

        using var host = builder.Build();

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("ALB0025");
        ex.Which.Message.Should().Contain("IProcessorLeaseManager");
        ex.Which.Message.Should().Contain(ModuleKey);
    }

    [Fact]
    public async Task InMemory_backend_with_leases_enabled_is_rejected_earlier_by_ALB0024()
    {
        // The in-memory backend never reaches the ALB0025 backstop: its own descriptor rejects
        // leases during options validation. Asserted here so that the two diagnostics stay
        // distinct — if ALB0024 were ever dropped, this would fail rather than silently
        // downgrade to the later, vaguer error.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto(ModuleKey, module => module
            .WithInMemory()
            .WithControlLoop(o => o with
            {
                Leases = o.Leases with { Enabled = true }
            }));

        using var host = builder.Build();

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>();
        ex.Which.Message.Should().Contain("ALB0024");
        ex.Which.Message.Should().NotContain("ALB0025");
    }
}

/// <summary>
/// Guards the fencing-context wiring in ControlLoopRegistration when the checkpoint store
/// implements <see cref="IFencableCheckpointStore"/>. Requires leases to be enabled so that
/// the fencing block is reached.
/// </summary>
public class FencingContextRegistrationTests
{
    private const string ModuleKey = "orders";

    // ─── Infrastructure ───────────────────────────────────────────────────────

    /// <summary>
    /// A checkpoint store that captures the arguments to SetFencingContext and
    /// SubscribeFenceViolation, delegating actual storage to InMemoryCheckpointStore.
    /// </summary>
    private sealed class CapturingFencableCheckpointStore : ICheckpointStore, IFencableCheckpointStore
    {
        private readonly InMemoryCheckpointStore _inner = new();

        public FencingContext? CapturedContext { get; private set; }
        public bool SubscribeFenceViolationWasCalled { get; private set; }

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default) =>
            _inner.GetAsync(processorId, ct);

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default) =>
            _inner.SaveAsync(processorId, position, ct);

        public Task ResetAsync(string processorId, CancellationToken ct = default) =>
            _inner.ResetAsync(processorId, ct);

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default) =>
            _inner.RewindAsync(processorId, position, ct);

        public void SetFencingContext(FencingContext ctx) => CapturedContext = ctx;

        public void SubscribeFenceViolation(Action<string> handler) =>
            SubscribeFenceViolationWasCalled = true;
    }

    // The same LeaseCapableBackend as in LeaseFencingRegistrationTests — repeated
    // here as a private type so it stays local to this test class.
    private sealed class LeaseCapableBackend : IAlbertoBackendDescriptor
    {
        private readonly InMemoryBackendDescriptor _storage = new();
        public string Name => "LeaseCapableFake";
        public bool SupportsTenancy => true;
        public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;
        public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => [];
        public void Register(AlbertoModuleContext context) => _storage.Register(context);
    }

    private sealed class CapturingLeaseManager : IProcessorLeaseManager
    {
        public TimeSpan LeaseDuration => TimeSpan.FromMinutes(1);
        public Task<IProcessorLease?> TryAcquireAsync(string consumerId, string processorId, string replicaId, CancellationToken ct = default) =>
            Task.FromResult<IProcessorLease?>(null);
        public Task<IReadOnlyList<string>> RenewLeasesAsync(string consumerId, string replicaId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task ReleaseAllLeasesAsync(string consumerId, string replicaId, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<ProcessorLeaseInfo>> GetAllLeasesAsync(string consumerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProcessorLeaseInfo>>([]);
    }

    private static async Task<CapturingFencableCheckpointStore> StartAndStopHostAsync(string? replicaId)
    {
        var fencableStore = new CapturingFencableCheckpointStore();
        var leaseManager = new CapturingLeaseManager();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto(ModuleKey, module => module
            .UseBackend(new LeaseCapableBackend())
            .WithControlLoop(o => o with
            {
                Leases = o.Leases with
                {
                    Enabled = true,
                    ReplicaId = replicaId,
                }
            }));

        // Override the checkpoint store with the capturing fencable one.
        // A later registration wins over the one the InMemory backend registered.
        builder.Services.AddKeyedSingleton<ICheckpointStore>(ModuleKey, fencableStore);
        builder.Services.AddKeyedSingleton<IProcessorLeaseManager>(ModuleKey, leaseManager);

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        return fencableStore;
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kills L154 NoCoverage Statement mutation — removes the SetFencingContext call.
    /// Without SetFencingContext, the checkpoint store's fencing context is never set.
    /// </summary>
    [Fact]
    public async Task SetFencingContext_is_called_when_checkpoint_store_is_fencable()
    {
        var store = await StartAndStopHostAsync(replicaId: "pod-1");

        store.CapturedContext.Should().NotBeNull(
            "SetFencingContext must be called so the checkpoint store knows the module key " +
            "and replica id when evaluating fence tokens");
    }

    /// <summary>
    /// Kills L157 NoCoverage Boolean mutation — UseProcessorLeaseFencing: true → false.
    /// The fencing context must declare that processor-lease fencing is in use.
    /// </summary>
    [Fact]
    public async Task FencingContext_UseProcessorLeaseFencing_is_true()
    {
        var store = await StartAndStopHostAsync(replicaId: "pod-1");

        store.CapturedContext!.UseProcessorLeaseFencing.Should().BeTrue(
            "processor-lease fencing must be activated so that checkpoint writes are fenced " +
            "against the lease generation rather than always proceeding");
    }

    /// <summary>
    /// Kills L157 from the other direction: the module key (as ConsumerId) and replica id
    /// must be forwarded to the FencingContext so claim_by records contain the right identity.
    /// </summary>
    [Fact]
    public async Task FencingContext_carries_the_module_key_and_replica_id()
    {
        var store = await StartAndStopHostAsync(replicaId: "pod-1");

        store.CapturedContext!.ConsumerId.Should().Be(ModuleKey);
        store.CapturedContext!.ReplicaId.Should().Be("pod-1");
    }

    /// <summary>
    /// Kills L165 NoCoverage Statement mutation — removes the SubscribeFenceViolation call.
    /// Without the subscription the group never receives fence-violation events and cannot
    /// stop dispatching side effects on a fenced-out replica.
    /// </summary>
    [Fact]
    public async Task SubscribeFenceViolation_is_called_when_checkpoint_store_is_fencable()
    {
        var store = await StartAndStopHostAsync(replicaId: "pod-1");

        store.SubscribeFenceViolationWasCalled.Should().BeTrue(
            "SubscribeFenceViolation must be called so the group stops when the lease is lost");
    }
}

/// <summary>
/// Guards the warning path in ControlLoopRegistration when the dead letter store does not
/// implement <see cref="IClaimableDeadLetterStore"/>. The retry loop cannot run without
/// claim-and-fence semantics; the registration logs a warning and returns an empty group.
/// </summary>
public class NonClaimableDeadLetterStoreRegistrationTests
{
    private const string ModuleKey = "orders";

    private sealed class NonClaimableDeadLetterStore : IDeadLetterStore
    {
        public Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(string processorId, string? tenantId = null, int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]);
        public Task<int> CountAsync(string processorId, CancellationToken ct = default) => Task.FromResult(0);
        public Task ClearAsync(string processorId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkForRetryAsync(string processorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => provider.Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Kills L207 NoCoverage Statement mutation — removes logger?.LogWarning(...)
    /// when the dead letter store is not IClaimableDeadLetterStore.
    /// The mutant silently skips the warning; this test verifies the warning is produced.
    /// </summary>
    [Fact]
    public async Task A_non_claimable_dead_letter_store_produces_a_startup_warning()
    {
        var loggingProvider = new CapturingLoggerProvider();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(loggingProvider);

        builder.Services.AddAlberto(ModuleKey, module => module.WithInMemory());

        // Override the dead letter store with one that does NOT implement IClaimableDeadLetterStore.
        // A later registration wins over the InMemory backend's registration.
        builder.Services.AddKeyedSingleton<IDeadLetterStore>(ModuleKey, new NonClaimableDeadLetterStore());

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        loggingProvider.Entries
            .Should().Contain(e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("IClaimableDeadLetterStore"),
            "a warning must be logged when the dead letter store cannot support automatic claim-and-fence retry");
    }

    [Fact]
    public async Task A_non_claimable_dead_letter_store_does_not_crash_at_startup()
    {
        // Belt-and-suspenders: the warning path must return an empty group, not throw.
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders(); // suppress noise in the test output

        builder.Services.AddAlberto(ModuleKey, module => module.WithInMemory());
        builder.Services.AddKeyedSingleton<IDeadLetterStore>(ModuleKey, new NonClaimableDeadLetterStore());

        using var host = builder.Build();

        var act = async () =>
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        };

        await act.Should().NotThrowAsync();
    }
}
