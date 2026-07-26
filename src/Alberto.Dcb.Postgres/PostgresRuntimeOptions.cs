using Alberto.Dcb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// The single place that knows how a module's effective <see cref="PostgresOptions"/> are found
/// once the host is running.
/// </summary>
/// <remarks>
/// A registration factory must not close over the options it was declared with: the configuration
/// overlay is applied after registration, so a value supplied only through
/// <c>Alberto:Modules:{moduleKey}:Postgres</c> would be lost. Every factory therefore reads the
/// module's current definition from <see cref="IOptionsMonitor{TOptions}"/> under the module key,
/// and falls back to the declared options when that definition carries no Postgres descriptor —
/// which happens in tests that never build a configuration.
/// </remarks>
/// <param name="moduleKey">The key the module's definition is registered under.</param>
/// <param name="declared">
/// The options the backend was declared with, used as the fallback.
/// </param>
internal sealed class PostgresRuntimeOptions(string moduleKey, PostgresOptions declared)
{
    /// <summary>
    /// Reads the module's current definition. Use when a factory needs more of the definition than
    /// its options — otherwise prefer <see cref="Resolve"/>.
    /// </summary>
    public AlbertoModuleDefinition Definition(IServiceProvider provider) =>
        provider.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey);

    /// <summary>Resolves the backend's effective options.</summary>
    public PostgresOptions Resolve(IServiceProvider provider) => For(Definition(provider));

    /// <summary>
    /// Resolves the backend's effective options together with the definition they came from, so a
    /// factory that also reads the definition does not fetch it twice.
    /// </summary>
    public (PostgresOptions Options, AlbertoModuleDefinition Definition) ResolveWithDefinition(
        IServiceProvider provider)
    {
        var definition = Definition(provider);
        return (For(definition), definition);
    }

    /// <summary>
    /// Resolves the options of the shard catalog's control database. The catalog is declared
    /// separately from the module's own backend, so it reads its own descriptor — but the fallback
    /// is the same, for the same reason.
    /// </summary>
    public PostgresOptions ResolveCatalog(IServiceProvider provider) =>
        Definition(provider).Tenancy.Catalog is PostgresBackendDescriptor catalog
            ? catalog.Options
            : declared;

    private PostgresOptions For(AlbertoModuleDefinition definition) =>
        definition.Backend is PostgresBackendDescriptor descriptor ? descriptor.Options : declared;
}
