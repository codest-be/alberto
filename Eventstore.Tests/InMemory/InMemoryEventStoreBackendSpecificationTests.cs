using EventStore;
using EventStore.InMemory;
using EventStore.MultiTenant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eventstore.Tests.Specifications;

/// <summary>
/// Concrete specification tests for InMemoryEventStoreBackend
/// </summary>
public class InMemoryEventStoreBackendSpecificationTests : EventStoreBackendSpecification
{
    private readonly ILogger<InMemoryEventStoreBackend> _logger =
        new NullLoggerFactory().CreateLogger<InMemoryEventStoreBackend>();

    private InMemoryEventStoreBackend? _backend;

    protected override Task<IEventStoreBackend> CreateBackend()
    {
        _backend = new InMemoryEventStoreBackend(_logger);
        return Task.FromResult<IEventStoreBackend>(_backend);
    }

    protected override Tenant CurrentTenant()
    {
        return new Tenant(1.ToString());
    }

    protected override Task SetupAsync()
    {
        return Task.CompletedTask;
    }

    protected override Task CleanupAsync()
    {
        _backend?.Clear();
        return Task.CompletedTask;
    }
}