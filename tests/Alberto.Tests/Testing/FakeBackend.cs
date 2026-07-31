using Alberto.Configuration;
using Microsoft.Extensions.Configuration;

namespace Alberto.Tests.Testing;

/// <summary>
/// A minimal <see cref="IAlbertoBackendDescriptor"/> for use in configuration unit tests.
/// Constructor parameters default to the most common variant across all callers.
/// </summary>
internal sealed class FakeBackend(bool supportsTenancy = true, params AlbertoValidationFailure[] failures)
    : IAlbertoBackendDescriptor
{
    /// <inheritdoc/>
    public string Name => "Fake";

    /// <inheritdoc/>
    public bool SupportsTenancy => supportsTenancy;

    /// <summary>Whether registration was invoked, and with what tenancy in effect.</summary>
    public bool Registered { get; private set; }

    /// <summary>
    /// Tenancy as seen at registration time. Order-dependent: a module that calls WithTenancy()
    /// after UseBackend() must still register a tenant-aware backend, which is exactly what
    /// ModuleDefinitionTests asserts.
    /// </summary>
    public bool? TenancyAtRegistration { get; private set; }

    /// <inheritdoc/>
    public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;

    /// <inheritdoc/>
    public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => failures;

    /// <inheritdoc/>
    public void Register(AlbertoModuleContext context)
    {
        Registered = true;
        TenancyAtRegistration = context.TenancyEnabled;
    }
}
