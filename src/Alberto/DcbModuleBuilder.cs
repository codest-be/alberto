using Alberto.Configuration;

namespace Alberto;

/// <summary>
/// Declares one Alberto module. Every call records intent into an immutable
/// <see cref="AlbertoModuleDefinition"/>; nothing is registered and no I/O happens until the
/// whole lambda has run. Call order therefore never changes the result.
/// </summary>
public sealed class DcbModuleBuilder
{
    private readonly List<Action<AlbertoModuleContext>> _deferredRegistrations = [];
    private readonly List<Action<TenancyBuilder>> _tenancyConfigurators = [];

    internal DcbModuleBuilder(string moduleKey) =>
        Definition = new AlbertoModuleDefinition { ModuleKey = moduleKey };

    /// <summary>The module key. Doubles as the DI service key for this module's services.</summary>
    public string ModuleKey => Definition.ModuleKey;

    internal AlbertoModuleDefinition Definition { get; private set; }

    internal IReadOnlyList<Action<AlbertoModuleContext>> DeferredRegistrations => _deferredRegistrations;

    internal bool HasTenancy => Definition.TenancyEnabled;

    internal bool ControlLoopConfigured { get; set; }

    /// <summary>
    /// Applies <paramref name="configure"/> to this module's declaration. This is the single
    /// mutation primitive; every <c>With*</c> extension is built on it.
    /// </summary>
    public DcbModuleBuilder Configure(Func<AlbertoModuleDefinition, AlbertoModuleDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Definition = configure(Definition)
            ?? throw new InvalidOperationException("A module configuration callback returned null.");

        return this;
    }

    /// <summary>
    /// Declares which storage backend this module uses. A module has exactly one backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">A backend was already declared.</exception>
    public DcbModuleBuilder UseBackend(IAlbertoBackendDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (Definition.Backend is { } existing)
        {
            throw new InvalidOperationException(
                $"Module '{ModuleKey}' already declares the {existing.Name} backend, so it cannot also " +
                $"use {descriptor.Name}. Each module has exactly one event store backend.");
        }

        return Configure(d => d with { Backend = descriptor });
    }

    /// <summary>Records a processor so startup validation can see it without resolving services.</summary>
    public DcbModuleBuilder DeclareProcessor(ProcessorDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        return Configure(d => d with { Processors = d.Processors.Add(declaration) });
    }

    /// <summary>
    /// Defers a service registration until the declaration is complete. The callback receives the
    /// final definition, so it can branch on tenancy or options that were declared later in the chain.
    /// </summary>
    public DcbModuleBuilder Register(Action<AlbertoModuleContext> register)
    {
        ArgumentNullException.ThrowIfNull(register);

        _deferredRegistrations.Add(register);
        return this;
    }

    /// <summary>
    /// Declares that this module's data is partitioned per tenant. The backend must support it.
    /// </summary>
    public DcbModuleBuilder WithTenancy() => Configure(d => d with { TenancyEnabled = true });

    /// <summary>
    /// Declares tenancy and configures how the tenants are laid out — in one database, which is
    /// the default, or across several.
    /// </summary>
    /// <example>
    /// <code>
    /// .WithTenancy(t => t.AcrossPostgresDatabases(s => s
    ///     .WithCatalog(o => o with { ConnectionString = catalogCs })
    ///     .AddShard("db1", o => o with { ConnectionString = db1Cs })
    ///     .AddShard("db2", o => o with { ConnectionString = db2Cs })
    ///     .WithDefaultShard("db1")))
    /// </code>
    /// </example>
    /// <remarks>
    /// <paramref name="configure"/> does not run here. It runs once the whole module lambda has
    /// returned, so a shard can inherit settings from a <c>.WithPostgres(...)</c> written after
    /// this call — the module builder's rule that call order never changes the outcome holds for
    /// tenancy too.
    /// </remarks>
    public DcbModuleBuilder WithTenancy(Action<TenancyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _tenancyConfigurators.Add(configure);
        return Configure(d => d with { TenancyEnabled = true });
    }

    /// <summary>
    /// Runs the deferred tenancy callbacks against the completed declaration. Called by
    /// <c>AddAlberto</c> once the module lambda has returned and the backend is final.
    /// </summary>
    internal void ApplyTenancy()
    {
        if (_tenancyConfigurators.Count == 0)
            return;

        var tenancy = new TenancyBuilder(ModuleKey, Definition.Backend);
        foreach (var configure in _tenancyConfigurators)
            configure(tenancy);

        Configure(d => d with { Tenancy = tenancy.Definition });
    }
}
