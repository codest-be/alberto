using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Testing;

/// <summary>
/// A running Alberto module over the in-memory backend, for testing application code.
/// </summary>
/// <remarks>
/// Alberto's control loop is asynchronous, so appending an event and asserting on a projection
/// in the next line asserts on a race. The harness exists to make the correct sequence — append,
/// wait for quiescence, assert — shorter than the incorrect one.
/// </remarks>
public sealed class AlbertoTestHarness : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _moduleKey;
    private readonly TimeProvider _timeProvider;

    private AlbertoTestHarness(IHost host, string moduleKey, TimeProvider timeProvider)
    {
        _host = host;
        _moduleKey = moduleKey;
        _timeProvider = timeProvider;
    }

    /// <summary>The running host's services. Resolve module services with the module key.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>The module key this harness was started with.</summary>
    public string ModuleKey => _moduleKey;

    /// <summary>Starts a module and its control loop.</summary>
    /// <param name="moduleKey">The module key, used for every keyed resolution.</param>
    /// <param name="configure">Configures the module exactly as production code would.</param>
    /// <param name="configureServices">Additional registrations, applied before the module.</param>
    /// <param name="timeProvider">Clock used for quiescence waits. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<AlbertoTestHarness> StartAsync(
        string moduleKey,
        Action<DcbModuleBuilder> configure,
        Action<IServiceCollection>? configureServices = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = Host.CreateApplicationBuilder();
        configureServices?.Invoke(builder.Services);
        builder.Services.AddAlberto(moduleKey, configure);

        var host = builder.Build();
        await host.StartAsync(ct).ConfigureAwait(false);

        return new AlbertoTestHarness(host, moduleKey, timeProvider ?? TimeProvider.System);
    }

    /// <summary>Appends one event to the module's store.</summary>
    /// <param name="payload">The event payload.</param>
    /// <param name="tags">Tags to attach. Defaults to none.</param>
    /// <param name="tenantId">Tenant to append under. Defaults to none (non-tenant mode).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AppendAsync<TEvent>(
        TEvent payload,
        IEnumerable<EventTag>? tags = null,
        string? tenantId = null,
        CancellationToken ct = default)
        where TEvent : IEvent
    {
        await using var scope = Services.CreateAsyncScope();
        if (tenantId is not null)
            scope.ServiceProvider.GetService<TenantContext>()?.SetTenant(tenantId);

        var store = scope.ServiceProvider.GetRequiredKeyedService<IEventStore>(_moduleKey);
        await store.AppendAsync(
            [TestEvents.NewEvent(payload, tags)], cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until every processor's checkpoint has reached the store head.
    /// </summary>
    /// <param name="timeout">How long to wait. Defaults to <see cref="Poll.DefaultTimeout"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="TimeoutException">
    /// Processing did not catch up. Deliberately loud: returning quietly would push the failure
    /// into an unrelated assertion later.
    /// </exception>
    public Task WaitForQuiescenceAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        => Poll.UntilAsync(
            IsQuiescentAsync,
            $"module '{_moduleKey}' to finish processing",
            timeout,
            timeProvider: _timeProvider,
            ct: ct);

    private async ValueTask<bool> IsQuiescentAsync()
    {
        var eventStore = Services.GetRequiredKeyedService<IEventStore>(_moduleKey);
        var head = await eventStore.GetLastPositionAsync().ConfigureAwait(false);

        // An empty store is trivially quiescent — nothing to catch up to.
        if (head == 0)
            return true;

        var definition = Services
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get(_moduleKey);

        // A module with no declared processors is trivially quiescent.
        if (definition.Processors.IsEmpty)
            return true;

        var checkpoints = Services.GetRequiredKeyedService<ICheckpointStore>(_moduleKey);

        foreach (var processor in definition.Processors)
        {
            var checkpoint = await checkpoints.GetAsync(processor.ProcessorId).ConfigureAwait(false);
            if ((checkpoint ?? 0) < head)
                return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }
}
