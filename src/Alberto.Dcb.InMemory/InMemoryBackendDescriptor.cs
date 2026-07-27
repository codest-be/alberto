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
    public bool SupportsTenancy => true;

    /// <inheritdoc />
    public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;

    /// <inheritdoc />
    public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition)
    {
        // A shared backend is registered as a singleton resolved from the owning module,
        // so it cannot carry per-request tenant state. Combining it with .WithTenancy() would
        // silently skip the tenant decorator and write TenantId = null for every event.
        if (SharedModuleKey is not null && definition.TenancyEnabled)
        {
            yield return new AlbertoValidationFailure(
                "ALB0017",
                $"The module shares the in-memory backend from '{SharedModuleKey}' but also declares " +
                ".WithTenancy(). A shared backend resolves a singleton from the owning module, which " +
                "conflicts with the scoped lifetime the tenant path requires.",
                "Remove .WithTenancy() from this module, or give it its own .WithInMemory() backend " +
                "instead of sharing.");
        }
    }

    /// <inheritdoc />
    public void Register(AlbertoModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InMemoryBuilderExtensions.RegisterBackend(context, SharedModuleKey);
    }
}
