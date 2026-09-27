using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Alberto.Testing;

/// <summary>
/// Checks a hand-written <see cref="IEventTypeRegistry"/> against the attributes on the event types
/// it registers: the id, version and opt-out each <see cref="EventTypeAttribute"/> declares, and
/// the tags each type's <see cref="TagAttribute"/> properties declare.
/// </summary>
/// <remarks>
/// <para>
/// A registry built with <see cref="EventTypeRegistry.CreateBuilder"/> restates the tags by hand,
/// and an extractor that disagrees with the attributes writes events a consistency boundary will
/// not match. The build cannot see inside the extractor, so this is the check: call it from one
/// test over the registry your application ships.
/// </para>
/// <para>
/// Tags are compared by running both the registry's extractor and the reflection path over the
/// same event. Pass real samples to <see cref="Verify"/> where you have them. For a registered type
/// with no sample, the verifier builds a probe: an uninitialized instance with every
/// <see cref="TagAttribute"/> property set to a distinct value (strings, <see cref="Guid"/>s,
/// integers and enums, and their nullable forms) and every other property left at its default.
/// A type whose tag properties are of another type, or whose extractor throws on the probe
/// (because it reads a property the probe left null), is reported with a request for a sample.
/// </para>
/// <para>
/// This uses reflection throughout. It is a test-time check, not something to run in a trimmed
/// or Native AOT application.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Fact]
/// public void Event_registry_matches_the_event_attributes()
///     =&gt; EventTypeRegistryVerifier.Verify(OrdersEvents.Registry);
/// </code>
/// </example>
public static class EventTypeRegistryVerifier
{
    private const string ReflectionMessage =
        "Reads [EventType] and [Tag] attributes and builds probe events by reflection. " +
        "Run it from a test, not from a trimmed or Native AOT application.";

    /// <summary>
    /// Verifies every descriptor in <paramref name="registry"/> and throws one
    /// <see cref="SpecificationException"/> listing every mismatch found.
    /// </summary>
    /// <param name="registry">The registry to check, usually the one your application passes to <c>WithEvents</c>.</param>
    /// <param name="samples">
    /// Events to compare tags over. A sample is used in place of the probe for its type; at most
    /// one sample per type. A sample of a type the registry does not register is a failure.
    /// </param>
    /// <exception cref="SpecificationException">At least one descriptor disagrees with its type's attributes.</exception>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static void Verify(IEventTypeRegistry registry, params IEvent[] samples)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(samples);

        var failures = new List<string>();
        var samplesByType = new Dictionary<Type, IEvent>();
        foreach (var sample in samples)
        {
            ArgumentNullException.ThrowIfNull(sample, nameof(samples));
            var type = sample.GetType();
            if (!registry.TryGetByType(type, out _))
                failures.Add($"{type.FullName}: a sample was given, but the registry does not register this type.");
            else if (!samplesByType.TryAdd(type, sample))
                throw new ArgumentException($"More than one sample of '{type.FullName}' was given.", nameof(samples));
        }

        var registered = EventSerializer.FromRegistry(registry);
        // An empty registry makes ExtractTags fall back to reading [Tag] properties by reflection,
        // which is exactly the behaviour a hand-written extractor has to reproduce.
        var reflected = EventSerializer.FromRegistry(EventTypeRegistry.CreateBuilder().Build());

        foreach (var descriptor in registry.Descriptors.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            VerifyEventType(descriptor, failures);

            var sample = samplesByType.GetValueOrDefault(descriptor.ClrType);
            if (sample is null && !TryProbe(descriptor.ClrType, out sample, out var whyNot))
            {
                failures.Add($"{Name(descriptor)}: cannot build a probe event ({whyNot}). Pass a sample of this type.");
                continue;
            }

            VerifyTags(descriptor, sample!, registered, reflected, fromSample: samplesByType.ContainsKey(descriptor.ClrType), failures);
        }

        if (failures.Count == 0)
            return;

        var message = new StringBuilder()
            .AppendLine("The event type registry disagrees with the attributes on its event types:");
        foreach (var failure in failures)
            message.Append("  - ").AppendLine(failure);
        throw new SpecificationException(message.ToString().TrimEnd());
    }

    private static void VerifyEventType(EventTypeDescriptor descriptor, List<string> failures)
    {
        // A type registered with an explicit id and no [EventType] has nothing to disagree with.
        var attr = EventTypeAttribute.GetEventType(descriptor.ClrType);
        if (attr is null)
            return;

        if (!string.Equals(attr.Id, descriptor.Id, StringComparison.Ordinal))
            failures.Add($"{Name(descriptor)}: registered as id '{descriptor.Id}', but [EventType] declares '{attr.Id}'.");
        if (attr.Version != descriptor.Version)
            failures.Add($"{Name(descriptor)}: registered at version {descriptor.Version}, but [EventType] declares version {attr.Version}.");
        if (attr.UpcastingNotRequired != descriptor.UpcastingNotRequired)
            failures.Add($"{Name(descriptor)}: registered with UpcastingNotRequired = {descriptor.UpcastingNotRequired}, but [EventType] declares {attr.UpcastingNotRequired}.");
    }

    [RequiresUnreferencedCode(ReflectionMessage)]
    private static void VerifyTags(
        EventTypeDescriptor descriptor,
        IEvent @event,
        EventSerializer registered,
        EventSerializer reflected,
        bool fromSample,
        List<string> failures)
    {
        IReadOnlyCollection<EventTag> actual;
        try
        {
            actual = registered.ExtractTags(@event);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(fromSample
                ? $"{Name(descriptor)}: the registered tag extractor threw {ex.GetType().Name} on the sample: {ex.Message}"
                : $"{Name(descriptor)}: the registered tag extractor threw {ex.GetType().Name} on a probe event " +
                  "that sets only the [Tag] properties. Pass a sample of this type.");
            return;
        }

        // The reserved version tag is stamped from the descriptor on one side and from the
        // attribute on the other; VerifyEventType already compares those, so leave it out here.
        var got = UserTags(actual);
        var want = UserTags(reflected.ExtractTags(@event));

        var missing = Subtract(want, got);
        var extra = Subtract(got, want);
        if (missing.Count == 0 && extra.Count == 0)
            return;

        var parts = new List<string>();
        if (missing.Count > 0)
            parts.Add($"declared by [Tag] but not extracted: {string.Join(", ", missing)}");
        if (extra.Count > 0)
            parts.Add($"extracted but not declared by [Tag]: {string.Join(", ", extra)}");
        failures.Add($"{Name(descriptor)}: tags disagree on the {(fromSample ? "sample" : "probe event")}; {string.Join("; ", parts)}.");
    }

    private static List<string> UserTags(IEnumerable<EventTag> tags)
        => tags.Where(t => !t.Concept.StartsWith(EventTag.ReservedConceptPrefix, StringComparison.Ordinal))
            .Select(t => t.ToString())
            .ToList();

    // Multiset difference, so a tag yielded twice where it is declared once is still reported.
    private static List<string> Subtract(List<string> from, List<string> remove)
    {
        var rest = new List<string>(from);
        foreach (var tag in remove)
            rest.Remove(tag);
        return rest;
    }

    [RequiresUnreferencedCode(ReflectionMessage)]
    private static bool TryProbe(Type type, [NotNullWhen(true)] out IEvent? probe, [NotNullWhen(false)] out string? whyNot)
    {
        probe = null;
        object instance;
        try
        {
            instance = RuntimeHelpers.GetUninitializedObject(type);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or MemberAccessException)
        {
            whyNot = $"it cannot be instantiated: {ex.Message}";
            return false;
        }

        var ordinal = 0;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = property.GetCustomAttribute<TagAttribute>();
            if (attr is null)
                continue;

            ordinal++;
            if (!TryProbeValue(property.PropertyType, attr.Concept, ordinal, out var value))
            {
                whyNot = $"tag property '{property.Name}' is of type '{property.PropertyType}', which the verifier cannot fill";
                return false;
            }

            if (!TrySet(instance, property, value))
            {
                whyNot = $"tag property '{property.Name}' has no setter or compiler-generated backing field";
                return false;
            }
        }

        probe = (IEvent)instance;
        whyNot = null;
        return true;
    }

    // Distinct per property, so an extractor that swaps two concepts, or reads the wrong property
    // for one, produces a different tag than the attributes declare.
    private static bool TryProbeValue(Type type, string concept, int ordinal, out object? value)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
            value = $"{concept}-probe-{ordinal}";
        else if (underlying == typeof(Guid))
            value = new Guid(0x0a1be470, 0, (short)ordinal, 0, 0, 0, 0, 0, 0, 0, (byte)ordinal);
        else if (underlying.IsEnum && Enum.GetValues(underlying) is { Length: > 0 } values)
            value = values.GetValue((ordinal - 1) % values.Length);
        else if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)
                 || underlying == typeof(uint) || underlying == typeof(ulong) || underlying == typeof(ushort)
                 || underlying == typeof(byte) || underlying == typeof(sbyte))
            value = Convert.ChangeType(100 + ordinal, underlying, System.Globalization.CultureInfo.InvariantCulture);
        else
        {
            value = null;
            return false;
        }

        return true;
    }

    [RequiresUnreferencedCode(ReflectionMessage)]
    private static bool TrySet(object instance, PropertyInfo property, object? value)
    {
        // An init-only setter is an ordinary setter to reflection.
        if (property.SetMethod is not null)
        {
            property.SetValue(instance, value);
            return true;
        }

        var field = property.DeclaringType?.GetField(
            $"<{property.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
            return false;

        field.SetValue(instance, value);
        return true;
    }

    private static string Name(EventTypeDescriptor descriptor) => $"'{descriptor.Id}' ({descriptor.ClrType.Name})";
}
