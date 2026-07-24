using Alberto.Dcb.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb;

/// <summary>
/// Declares one Alberto module. Every call records intent into an immutable
/// <see cref="AlbertoModuleDefinition"/>; nothing is registered and no I/O happens until the
/// whole lambda has run. Call order therefore never changes the result.
/// </summary>
public sealed class DcbModuleBuilder
{
    private readonly List<Action<AlbertoModuleContext>> _deferredRegistrations = [];
    private readonly IServiceCollection _services;

    internal DcbModuleBuilder(IServiceCollection services, string moduleKey)
    {
        _services = services;
        Definition = new AlbertoModuleDefinition { ModuleKey = moduleKey };
    }

    /// <summary>The module key. Doubles as the DI service key for this module's services.</summary>
    public string ModuleKey => Definition.ModuleKey;

    /// <summary>
    /// The application's service collection.
    /// </summary>
    [Obsolete("Registering services directly makes configuration order-dependent. " +
              "Use Register(context => ...) for a deferred registration, or implement " +
              "IAlbertoBackendDescriptor for a storage backend. This property is removed in 1.0.")]
    public IServiceCollection Services => _services;

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
}
