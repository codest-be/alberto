using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// A storage backend as declared by <c>.WithPostgres(...)</c>, <c>.WithInMemory()</c> and friends.
/// This is the supported extension point for third-party backends; it replaces reaching into
/// <c>DcbModuleBuilder.Services</c> at declaration time.
/// </summary>
/// <remarks>
/// Implementations are immutable value objects. <see cref="ApplyConfiguration"/> returns a new
/// instance rather than mutating, so a backend can be declared before or after any other call in
/// the module lambda without changing the result.
/// </remarks>
public interface IAlbertoBackendDescriptor
{
    /// <summary>Human-readable backend name, used in validation messages. For example "Postgres".</summary>
    string Name { get; }

    /// <summary>Whether this backend can serve a module declared with <c>.WithTenancy()</c>.</summary>
    bool SupportsTenancy { get; }

    /// <summary>
    /// Overlays this backend's own options from the module's configuration section
    /// (already scoped to <c>Alberto:Modules:{moduleKey}</c>). Return <c>this</c> when there is
    /// nothing to bind.
    /// </summary>
    IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection);

    /// <summary>
    /// Reports backend-specific configuration problems. Called during startup validation,
    /// before any service is resolved, so it must not open connections or touch the container.
    /// </summary>
    IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition);

    /// <summary>
    /// Registers the backend's services. Called once, after validation, with the final definition.
    /// </summary>
    void Register(AlbertoModuleContext context);

    /// <summary>
    /// Returns the name of the configuration sub-section this backend reads from, and the
    /// <see cref="IAlbertoOverrides{TOptions}"/> type that enumerates its legal property names.
    /// Used by the unknown-key detector (<c>ALB0008</c>) to know which keys are intentional.
    /// </summary>
    /// <returns>
    /// A tuple whose first element is the section name (for example <c>"Postgres"</c>) and whose
    /// second element is the overrides type (for example <c>typeof(PostgresOverrides)</c>).
    /// Return <c>(null, null)</c> when the backend has no backend-specific configuration section.
    /// </returns>
    (string? SectionName, Type? OverridesType) GetConfigurationSection() => (null, null);
}
