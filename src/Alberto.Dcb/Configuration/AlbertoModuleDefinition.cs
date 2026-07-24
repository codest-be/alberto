using System.Collections.Immutable;
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

        return definition with
        {
            ControlLoop = AlbertoOptionsOverlay.Overlay<ControlLoopOptions, ControlLoopOverrides>(
                section, "ControlLoop", definition.ControlLoop),
            Telemetry = AlbertoOptionsOverlay.Overlay<TelemetryOptions, TelemetryOverrides>(
                section, "Telemetry", definition.Telemetry),
            Checkpoints = AlbertoOptionsOverlay.Overlay<CheckpointOptions, CheckpointOverrides>(
                section, "Checkpoints", definition.Checkpoints),
            Backend = definition.Backend?.ApplyConfiguration(section),
        };
    }
}
