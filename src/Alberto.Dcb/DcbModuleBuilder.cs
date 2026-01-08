using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb;

/// <summary>
/// Fluent builder for configuring an Alberto DCB module.
/// Supports keyed services for modular monolith isolation.
/// </summary>
public sealed class DcbModuleBuilder
{
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
}
