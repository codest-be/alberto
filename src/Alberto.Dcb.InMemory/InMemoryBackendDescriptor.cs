using Alberto.Dcb.Configuration;
using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// Declares the in-memory event store backend. Intended for tests, samples and local
/// development: state lives for the lifetime of the process and nothing is durable.
/// </summary>
public sealed record InMemoryBackendDescriptor : IAlbertoBackendDescriptor
{
    /// <summary>
    /// When set, this module reads and writes another module's in-memory store instead of its
    /// own, so several modules can share one event log in a test.
    /// </summary>
    public string? SharedModuleKey { get; init; }

    /// <inheritdoc />
    public string Name => "InMemory";

    /// <inheritdoc />
    public bool SupportsTenancy => false;

    /// <inheritdoc />
    public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;

    /// <inheritdoc />
    public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => [];

    /// <inheritdoc />
    public void Register(AlbertoModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InMemoryBuilderExtensions.RegisterBackend(context, SharedModuleKey);
    }
}
