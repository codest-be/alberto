using Alberto.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Tests.SampleEvents;

// A backend written the way an outside assembly would write one: this project has no
// InternalsVisibleTo from Alberto, so it can only use public API.

/// <summary>A do-nothing backend that counts the appends that reach it.</summary>
public sealed class CountingBackend : IEventStoreBackend, IEventStoreHeadBackend
{
    public int Appends { get; private set; }

    private static readonly Task<IReadOnlyCollection<IEventEnvelope>> None =
        Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);

    public Task<IReadOnlyCollection<IEventEnvelope>> ReadAsync(
        DcbQuery query, long afterPosition, int limit, CancellationToken cancellationToken = default) => None;

    public Task<IReadOnlyCollection<IEventEnvelope>> ReadAllAsync(
        long afterPosition, int limit, CancellationToken cancellationToken = default) => None;

    public Task<IReadOnlyCollection<IEventEnvelope>> ReadAllOfTypesAsync(
        IReadOnlySet<string> eventTypes, long afterPosition, int limit,
        CancellationToken cancellationToken = default) => None;

    public IAsyncEnumerable<IEventEnvelope> StreamAsync(
        DcbQuery query, long afterPosition = 0, CancellationToken cancellationToken = default) =>
        StreamAllAsync(afterPosition, cancellationToken);

    public IAsyncEnumerable<IEventEnvelope> StreamAllAsync(
        long afterPosition = 0, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<IEventEnvelope>();

    public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events, DcbQuery? dcbQuery = null, long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        Appends++;
        return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);
    }

    public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);

    public Task<IReadOnlyList<long>> GetPositionsAsync(
        long afterPosition, int windowSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<long>>([]);
}

/// <summary>Declares <see cref="CountingBackend"/> through the public registration helper.</summary>
public sealed class CountingBackendDescriptor : IAlbertoBackendDescriptor
{
    public CountingBackend Backend { get; } = new();

    public string Name => "Counting";
    public bool SupportsTenancy => false;
    public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;
    public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => [];

    public void Register(AlbertoModuleContext context) =>
        context.AddEventStoreBackend(ServiceLifetime.Singleton, _ => Backend);
}
