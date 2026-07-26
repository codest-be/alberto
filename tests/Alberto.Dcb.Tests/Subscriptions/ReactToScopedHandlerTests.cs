using System.Text.Json;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Verifies that ReactTo&lt;TEvent, THandler&gt; resolves handlers per-event in a new scope,
/// allowing scoped handlers with unit-of-work dependencies (e.g. DbContext) to work correctly.
/// </summary>
public class ReactToScopedHandlerTests
{
    [EventType("item-processed")]
    public record ItemProcessed(string Name) : IEvent;

    public sealed class ReactorContextCapture
    {
        public DateTimeOffset? TimestampUtc { get; set; }
    }

    /// <summary>A scoped handler that records how many times it was instantiated.</summary>
    public sealed class ScopedHandler : IDisposable
    {
        private readonly List<int> _instanceLog;
        private readonly int _instanceId;

        public ScopedHandler(InstanceCounter counter, List<string> handled)
        {
            _instanceId = counter.Next();
            _instanceLog = counter.InstanceLog;
            Handled = handled;
        }

        public List<string> Handled { get; }

        public Task Handle(ItemProcessed e, CancellationToken ct)
        {
            Handled.Add(e.Name);
            _instanceLog.Add(_instanceId);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    /// <summary>Singleton service that tracks how many ScopedHandler instances were created.</summary>
    public sealed class InstanceCounter
    {
        private int _count;
        public List<int> InstanceLog { get; } = [];
        public int Next() => Interlocked.Increment(ref _count);
    }

    public sealed class TenantCapture
    {
        public string? TenantId { get; set; }
    }

    public sealed class TenantAwareHandler(
        ITenantAccessor tenantAccessor,
        TenantCapture capture)
    {
        public Task Handle(ItemProcessed e, CancellationToken ct)
        {
            capture.TenantId = tenantAccessor.TenantId;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ScopedHandler_IsResolvedOncePerEvent()
    {
        var handled = new List<string>();
        var counter = new InstanceCounter();

        var services = new ServiceCollection();
        services.AddSingleton(counter);
        services.AddSingleton(handled);
        services.AddScoped<ScopedHandler>();

        services.AddAlberto("test", builder => builder
            .ReactTo<ItemProcessed, ScopedHandler>(h => h.Handle, "test-reactor"));

        var provider = services.BuildServiceProvider(validateScopes: true);
        var processor = provider.GetKeyedServices<IEventProcessor>("test").Single();

        await processor.ProcessEventAsync(CreateEnvelope(new ItemProcessed("first"), 1), TestContext.Current.CancellationToken);
        await processor.ProcessEventAsync(CreateEnvelope(new ItemProcessed("second"), 2), TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], handled);

        // Two events → two scopes → two distinct handler instances
        Assert.Equal(2, counter.InstanceLog.Count);
        Assert.NotEqual(counter.InstanceLog[0], counter.InstanceLog[1]);
    }

    [Fact]
    public async Task ScopedHandler_TryAddScoped_DoesNotOverrideExplicitRegistration()
    {
        // Pre-register as scoped before calling ReactTo — TryAddScoped must not replace it.
        var handled = new List<string>();
        var counter = new InstanceCounter();

        var services = new ServiceCollection();
        services.AddSingleton(counter);
        services.AddSingleton(handled);
        services.AddScoped<ScopedHandler>(); // explicit registration

        services.AddAlberto("test", builder => builder
            .ReactTo<ItemProcessed, ScopedHandler>(h => h.Handle, "test-reactor"));

        // Verify there is exactly one registration for ScopedHandler
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(ScopedHandler)));

        var provider = services.BuildServiceProvider(validateScopes: true);
        var processor = provider.GetKeyedServices<IEventProcessor>("test").Single();

        await processor.ProcessEventAsync(CreateEnvelope(new ItemProcessed("ok"), 1), TestContext.Current.CancellationToken);

        Assert.Equal(["ok"], handled);
    }

    [Fact]
    public async Task ScopedHandler_ReceivesTenantFromEventEnvelope()
    {
        var capture = new TenantCapture();
        var services = new ServiceCollection();
        services.AddTenancy();
        services.AddSingleton(capture);

        services.AddAlberto("test", builder => builder
            .ReactTo<ItemProcessed, TenantAwareHandler>(
                h => h.Handle,
                "tenant-aware-reactor"));

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var processor = provider.GetKeyedServices<IEventProcessor>("test").Single();

        await processor.ProcessEventAsync(
            CreateEnvelope(new ItemProcessed("tenant"), 1, tenantId: "tenant_a"),
            TestContext.Current.CancellationToken);

        Assert.Equal("tenant_a", capture.TenantId);
    }

    [Fact]
    public async Task DependencyOverload_CanAccessReactorContextMetadata()
    {
        var capture = new ReactorContextCapture();
        var createdAt = new DateTime(2026, 04, 16, 12, 34, 56, DateTimeKind.Utc);

        var services = new ServiceCollection();
        services.AddSingleton(capture);

        services.AddAlberto("test", builder => builder
            .ReactTo<ItemProcessed>(
                sp =>
                {
                    var state = sp.GetRequiredService<ReactorContextCapture>();
                    return (_, context, ct) =>
                    {
                        state.TimestampUtc = context.Timestamp;
                        return Task.CompletedTask;
                    };
                },
                "test-reactor"));

        var provider = services.BuildServiceProvider(validateScopes: true);
        var processor = provider.GetKeyedServices<IEventProcessor>("test").Single();

        await processor.ProcessEventAsync(CreateEnvelope(new ItemProcessed("timestamp"), 1, createdAt), TestContext.Current.CancellationToken);

        Assert.Equal(new DateTimeOffset(createdAt, TimeSpan.Zero), capture.TimestampUtc);
    }

    [Fact]
    public void ReactTo_RegistersExecutionOptionsForProcessor()
    {
        var services = new ServiceCollection();

        services.AddAlberto("test", builder => builder
            .ReactTo<ItemProcessed>(
                _ => (_, _) => Task.CompletedTask,
                "batched-reactor",
                configure: o => o with { BatchingMode = ProcessorBatchingMode.Required }));

        var provider = services.BuildServiceProvider(validateScopes: true);
        var processor = provider.GetKeyedServices<IEventProcessor>("test").Single();
        var registration = provider
            .GetKeyedServices<ProcessorExecutionRegistration>("test")
            .Single();

        Assert.IsAssignableFrom<IBatchableProcessor>(processor);
        Assert.Equal("batched-reactor", registration.ProcessorId);
        Assert.Equal(ProcessorBatchingMode.Required, registration.Options.BatchingMode);
    }

    [Fact]
    public void ReactTo_SyncModeRejectsBatchingConfiguration()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddAlberto("test", builder => builder.ReactTo<ItemProcessed>(
                _ => (_, _) => Task.CompletedTask,
                "sync-reactor",
                ReactorMode.Sync,
                o => o with { BatchingMode = ProcessorBatchingMode.IfSupported })));

        Assert.Contains("cannot enable async batching", exception.Message);
    }

    private static IEventEnvelope CreateEnvelope<TEvent>(
        TEvent @event,
        long position,
        DateTime? createdAt = null,
        string tenantId = "test_tenant")
        where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GlobalPosition = position,
            EventType = new EventType(eventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event),
            Metadata = new Dictionary<string, string>(),
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
    }
}
