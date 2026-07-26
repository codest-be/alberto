using System.Collections.Immutable;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Upcasting;
using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// The complete, immutable declaration of one Alberto module: what backend it uses, what
/// processors it runs, and how the control loop behaves. Registered as a named options
/// instance keyed by <see cref="ModuleKey"/> and validated at startup.
/// </summary>
public sealed record AlbertoModuleDefinition
{
    /// <summary>The module key passed to <c>AddAlberto</c>. Also the DI service key.</summary>
    public string ModuleKey { get; internal set; } = string.Empty;

    /// <summary>Whether <c>.WithTenancy()</c> was called.</summary>
    public bool TenancyEnabled { get; internal set; }

    /// <summary>
    /// How this module's tenants are laid out. Default — and what <c>.WithTenancy()</c> with no
    /// argument produces — is one database for every tenant.
    /// </summary>
    public TenancyDefinition Tenancy { get; internal set; } = new();

    /// <summary>
    /// The shard this definition describes, or null for the module as a whole. A sharded module
    /// produces one definition per shard alongside the logical one, and only the shard copies
    /// register services.
    /// </summary>
    public string? ShardId { get; internal set; }

    /// <summary>
    /// The DI service key this definition's services are registered under: the module key, or
    /// <c>module#shard</c> for a shard. This — not <see cref="ModuleKey"/> — is the key to look
    /// services up by and the name of this definition's options instance.
    /// </summary>
    // Fully qualified: the Tenancy property above shadows the namespace of the same name.
    public string ServiceKey =>
        ShardId is null ? ModuleKey : global::Alberto.Dcb.Tenancy.ShardKey.Compose(ModuleKey, ShardId);

    /// <summary>The module key, with the shard appended when there is one. For messages.</summary>
    public string DisplayName => ShardId is null ? ModuleKey : $"{ModuleKey} (shard '{ShardId}')";

    /// <summary>The declared storage backend, or null when none was declared.</summary>
    public IAlbertoBackendDescriptor? Backend { get; internal set; }

    /// <summary>Control loop settings.</summary>
    public ControlLoopOptions ControlLoop { get; internal set; } = new();

    /// <summary>Telemetry settings. Only meaningful when <c>.WithTelemetry()</c> was called.</summary>
    public TelemetryOptions Telemetry { get; internal set; } = new();

    /// <summary>Checkpoint hygiene settings.</summary>
    public CheckpointOptions Checkpoints { get; internal set; } = new();

    /// <summary>Whether <c>.WithTelemetry()</c> was called.</summary>
    public bool TelemetryEnabled { get; internal set; }

    /// <summary>Every processor declared on this module.</summary>
    public ImmutableArray<ProcessorDeclaration> Processors { get; internal set; } = [];

    /// <summary>
    /// Configuration keys found under <see cref="ConfigurationPath"/> that do not correspond to
    /// any known <see cref="IAlbertoOverrides{TOptions}"/> property. Populated by
    /// <see cref="ApplyConfiguration"/>; surfaced by <see cref="AlbertoModuleValidator"/> as
    /// <c>ALB0008</c> failures at startup.
    /// </summary>
    public ImmutableArray<UnknownConfigurationKey> UnknownConfigurationKeys { get; internal set; } = [];

    /// <summary>
    /// Upcaster declarations accumulated via <c>.AddUpcaster()</c> calls. Consumed at Phase 3
    /// registration to wire the upcaster chain into the module's <see cref="EventSerializer"/>.
    /// Because all <c>Configure</c> callbacks run before any <c>Register</c> callback, adding
    /// upcasters before or after <c>WithEventsFrom</c> produces the same result.
    /// </summary>
    public ImmutableArray<UpcasterDeclaration> UpcasterDeclarations { get; internal set; } = [];

    /// <summary>
    /// Event type id / schema-version pairs extracted from the assembly that was passed to
    /// <c>WithEventsFrom()</c>. Populated eagerly during Phase 1 so
    /// <see cref="AlbertoModuleValidator"/> can verify upcaster coverage at startup without
    /// retaining a reference to an <see cref="System.Reflection.Assembly"/>.
    /// </summary>
    public ImmutableArray<RegisteredEventType> RegisteredEventTypes { get; internal set; } = [];

    /// <summary>The configuration path this module binds from.</summary>
    public string ConfigurationPath => $"Alberto:Modules:{ModuleKey}";

    /// <summary>
    /// Returns <paramref name="definition"/> with every value found under
    /// <c>Alberto:Modules:{ModuleKey}</c> applied on top of the code-configured defaults.
    /// </summary>
    public static AlbertoModuleDefinition ApplyConfiguration(
        AlbertoModuleDefinition definition,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(definition.ConfigurationPath);

        var processorsSection = section.GetSection("Processors");
        var overlaidProcessors = definition.Processors.Select(processor =>
        {
            var procSection = processorsSection.GetSection(processor.ProcessorId);
            if (!procSection.Exists())
                return processor;

            return processor with
            {
                Execution = AlbertoOptionsOverlay.Overlay<ProcessorExecutionOptions, ProcessorExecutionOverrides>(
                    processorsSection, processor.ProcessorId, processor.Execution),
            };
        }).ToImmutableArray();

        // Scan for unknown configuration keys and record them for ALB0008 validation.
        var unknownKeys = AlbertoConfigurationScanner.Scan(section, definition.Backend);

        var boundBackend = definition.Backend?.ApplyConfiguration(section);

        return definition with
        {
            Tenancy = TenancyConfiguration.Apply(definition.Tenancy, boundBackend, section),
            ControlLoop = AlbertoOptionsOverlay.Overlay<ControlLoopOptions, ControlLoopOverrides>(
                section, "ControlLoop", definition.ControlLoop),
            Telemetry = AlbertoOptionsOverlay.Overlay<TelemetryOptions, TelemetryOverrides>(
                section, "Telemetry", definition.Telemetry),
            Checkpoints = AlbertoOptionsOverlay.Overlay<CheckpointOptions, CheckpointOverrides>(
                section, "Checkpoints", definition.Checkpoints),
            Backend = boundBackend,
            Processors = overlaidProcessors,
            UnknownConfigurationKeys = unknownKeys,
        };
    }
}

/// <summary>
/// An event-type ID / schema-version pair recorded during the assembly scan performed by
/// <c>WithEventsFrom()</c>. Used by <see cref="AlbertoModuleValidator"/> to cross-check
/// upcaster coverage at startup.
/// </summary>
public readonly record struct RegisteredEventType(string Id, int Version);
