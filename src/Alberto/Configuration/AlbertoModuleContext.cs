using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Configuration;

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

    /// <summary>
    /// The DI service key to register this module's services under, and the name of its options
    /// instance. Equal to <c>Definition.ModuleKey</c> for an ordinary module; for one shard of a
    /// sharded module it is <c>module#shard</c>, so the same registration code produces a
    /// separate set of services per shard without knowing that sharding exists.
    /// </summary>
    public string ModuleKey => Definition.ServiceKey;

    /// <summary>The logical module key, without the shard. Equal to <see cref="ModuleKey"/> when unsharded.</summary>
    public string LogicalModuleKey => Definition.ModuleKey;

    /// <summary>The shard being registered, or null when this module is not sharded.</summary>
    public string? ShardId => Definition.ShardId;

    /// <summary>Shorthand for <c>Definition.TenancyEnabled</c>.</summary>
    public bool TenancyEnabled => Definition.TenancyEnabled;
}
