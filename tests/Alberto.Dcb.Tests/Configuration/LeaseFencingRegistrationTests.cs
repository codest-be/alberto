using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Alberto.Dcb.Tests.Configuration;

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
