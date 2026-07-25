using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// What a deferred registration callback receives. Created once per module, after the
/// declaration lambda has run — so all builder calls are visible regardless of their
/// order inside the lambda. <see cref="Definition"/> reflects the code-configured state;
/// configuration overlay is applied later at host startup when the named
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/> instance resolves.
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
