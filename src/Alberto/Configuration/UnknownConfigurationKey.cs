namespace Alberto.Configuration;

/// <summary>
/// A configuration key found under a module's section that does not match any known option.
/// Produced by <see cref="AlbertoModuleDefinition.ApplyConfiguration"/> and surfaced by
/// <see cref="AlbertoModuleValidator"/> as <c>ALB0008</c>.
/// </summary>
/// <param name="FullKey">
/// The full colon-delimited configuration path, for example
/// <c>Alberto:Modules:orders:ControlLoop:BatchSizes</c>.
/// </param>
/// <param name="Suggestion">
/// The closest known legal leaf name at the same nesting level, or <c>null</c> when
/// no candidate falls within the edit-distance threshold.
/// When non-null, surfaced as <c>→ Did you mean '{Suggestion}'?</c> in the
/// <c>ALB0008</c> remedy.
/// </param>
public sealed record UnknownConfigurationKey(string FullKey, string? Suggestion);
