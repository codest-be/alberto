using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Alberto;

/// <summary>
/// The reflection-based JSON contracts Alberto falls back to when it reads an envelope without a
/// registry: a module configured with no <see cref="EventSerializer"/>, or an
/// <see cref="Evolver{TState}"/> folding raw envelopes.
/// </summary>
/// <remarks>
/// <para>
/// Both fallbacks read with the options a serializer gets by default
/// (<see cref="EventSerializer.CreateDefaultOptions"/>), not with System.Text.Json's own defaults.
/// They used to disagree on property-name casing, so a payload one read path accepted could come
/// out of the other with members at their defaults.
/// </para>
/// <para>
/// The reflection resolver is only reached when System.Text.Json's reflection feature switch is
/// on. Trimmed and Native AOT applications turn it off by default; the trimmer then removes the
/// branch, and the call throws a message naming the fix instead — register the module's event
/// types with <c>WithEvents(registry)</c> so reads go through their <see cref="JsonTypeInfo"/>.
/// This is the same guard System.Text.Json applies to its own reflection path.
/// </para>
/// </remarks>
internal static class ReflectionJson
{
    private const string Justification =
        "Guarded by JsonSerializer.IsReflectionEnabledByDefault, a feature switch that trimmed and " +
        "Native AOT applications turn off; the trimmer then removes the reflection branch and the " +
        "method throws a message pointing at WithEvents(registry) instead.";

    private static readonly Lazy<JsonSerializerOptions> FallbackOptions = new(CreateFallbackOptions);

    /// <summary>
    /// The contract for <paramref name="type"/> under the default serializer options, resolved by
    /// reflection. <paramref name="eventTypeId"/> is only used in the error message.
    /// </summary>
    /// <exception cref="InvalidOperationException">Reflection-based serialization is disabled.</exception>
    public static JsonTypeInfo GetFallbackTypeInfo(Type type, string eventTypeId)
    {
        if (!JsonSerializer.IsReflectionEnabledByDefault)
            throw new InvalidOperationException(
                $"Event '{eventTypeId}' was read without an event type registry, which needs " +
                "reflection-based System.Text.Json, and this application has it disabled (it is " +
                "trimmed or published as Native AOT). Register the module's event types with " +
                ".WithEvents(registry) so reads use each type's JsonTypeInfo, and fold with the " +
                $"command pipeline rather than Evolver.Evolve/Reconstitute over raw envelopes. Type: '{type.FullName}'.");

        return FallbackOptions.Value.GetTypeInfo(type);
    }

    /// <summary>
    /// Fills in the reflection resolver on options that have none, as <c>JsonSerializer</c>'s
    /// type-based overloads do on first use, so <see cref="JsonSerializerOptions.GetTypeInfo"/>
    /// can resolve from them. Options that are already read-only have a resolver and are left alone.
    /// </summary>
    [RequiresUnreferencedCode("Populates the reflection-based JSON resolver.")]
    [RequiresDynamicCode("Populates the reflection-based JSON resolver.")]
    public static JsonSerializerOptions EnsureResolver(JsonSerializerOptions options)
    {
        if (!options.IsReadOnly)
            options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = Justification)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = Justification)]
    private static JsonSerializerOptions CreateFallbackOptions()
        => EnsureResolver(EventSerializer.CreateDefaultOptions());
}
