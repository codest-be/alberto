using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb;

/// <summary>
/// Fluent builder for configuring an Alberto DCB module.
/// Supports keyed services for modular monolith isolation.
/// </summary>
public sealed class DcbModuleBuilder
{
    private bool _withTenancy;

    /// <summary>
    /// The service collection to register services with.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// The unique key identifying this module. Used for keyed service registration.
    /// </summary>
    public string ModuleKey { get; }

    internal DcbModuleBuilder(IServiceCollection services, string moduleKey)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        ModuleKey = moduleKey ?? throw new ArgumentNullException(nameof(moduleKey));
    }

    /// <summary>
    /// Enables multi-tenant mode for this module.
    /// In multi-tenant mode, tenant scoping is handled transparently by a decorator
    /// that reads the current tenant from <see cref="Tenancy.ITenantAccessor"/>.
    /// Tenancy services are registered automatically.
    /// </summary>
    /// <returns>The module builder for chaining.</returns>
    public DcbModuleBuilder WithTenancy()
    {
        _withTenancy = true;
        return this;
    }

    /// <summary>
    /// Gets whether multi-tenant mode has been enabled.
    /// </summary>
    internal bool HasTenancy => _withTenancy;

    /// <summary>
    /// Gets or sets whether a control loop has been explicitly configured.
    /// </summary>
    internal bool ControlLoopConfigured { get; set; }
}
