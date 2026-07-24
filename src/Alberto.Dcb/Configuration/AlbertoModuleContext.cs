using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// What a deferred registration callback receives. Created once per module, after the
/// declaration is complete and configuration has been overlaid — so
/// <see cref="Definition"/> is final and reading it is never order-dependent.
/// </summary>
public sealed class AlbertoModuleContext
{
    internal AlbertoModuleContext(IServiceCollection services, AlbertoModuleDefinition definition)
    {
        Services = services;
        Definition = definition;
    }

    /// <summary>The application's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The complete, immutable module declaration.</summary>
    public AlbertoModuleDefinition Definition { get; }

    /// <summary>Shorthand for <c>Definition.ModuleKey</c>. Use it as the DI service key.</summary>
    public string ModuleKey => Definition.ModuleKey;

    /// <summary>Shorthand for <c>Definition.TenancyEnabled</c>.</summary>
    public bool TenancyEnabled => Definition.TenancyEnabled;
}
