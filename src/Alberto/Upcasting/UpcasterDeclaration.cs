using System.Text.Json;

namespace Alberto.Upcasting;

// ---------------------------------------------------------------------------
// Internal step representation
// ---------------------------------------------------------------------------

/// <summary>
/// A single link in an upcast chain.
/// </summary>
internal sealed record UpcasterStep(
    // The source version this step converts FROM.
    int FromVersion,
    // The CLR type expected at this version (for JSON deserialization).
    Type FromType,
    // Converts an instance of FromType to the next version's object.
    Func<object, object> Transform);

// ---------------------------------------------------------------------------
// Public declaration
// ---------------------------------------------------------------------------

/// <summary>
/// An immutable, validated upcast chain for a single event type.
/// Produced by <see cref="DeclareUpcaster"/> and registered via <see cref="UpcasterRegistry"/>.
/// </summary>
public sealed class UpcasterDeclaration
{
    private readonly IReadOnlyList<UpcasterStep> _steps;

    internal UpcasterDeclaration(string eventTypeId, IReadOnlyList<UpcasterStep> steps)
    {
        EventTypeId = eventTypeId;
        _steps = steps;
        // Current version is one higher than the last step's source version.
        CurrentVersion = steps.Count > 0 ? steps[^1].FromVersion + 1 : 1;
    }

    /// <summary>The event type ID this upcaster applies to (e.g., "order-placed").</summary>
    public string EventTypeId { get; }

    /// <summary>
    /// The version that the chain produces on output.  An event whose
    /// <see cref="EventType.Version"/> equals <see cref="CurrentVersion"/> needs no upcasting.
    /// </summary>
    public int CurrentVersion { get; }

    /// <summary>
    /// Applies the upcast chain starting at <paramref name="fromVersion"/> and returns the
    /// final <see cref="IEvent"/> at <see cref="CurrentVersion"/>.
    /// </summary>
    /// <param name="fromVersion">The schema version recorded on the envelope.</param>
    /// <param name="json">The raw event JSON from the envelope.</param>
    /// <param name="options">JSON serializer options (from <see cref="EventSerializer"/>).</param>
    internal IEvent Apply(int fromVersion, string json, JsonSerializerOptions options)
    {
        // Find the first step that handles fromVersion.
        var startIndex = -1;
        for (var i = 0; i < _steps.Count; i++)
        {
            if (_steps[i].FromVersion == fromVersion)
            {
                startIndex = i;
                break;
            }
        }

        if (startIndex < 0)
            throw new InvalidOperationException(
                $"Upcaster for '{EventTypeId}' has no step for version {fromVersion}. " +
                $"Registered steps cover versions {string.Join(", ", _steps.Select(s => s.FromVersion))}.");

        // First step: deserialize JSON → step.FromType, then transform.
        var firstStep = _steps[startIndex];
        object current = JsonSerializer.Deserialize(json, firstStep.FromType, options)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize '{EventTypeId}' v{fromVersion} as {firstStep.FromType.Name}.");
        current = firstStep.Transform(current);

        // Subsequent steps: current is the CLR object from the previous step.
        for (var i = startIndex + 1; i < _steps.Count; i++)
        {
            current = _steps[i].Transform(current);
        }

        if (current is not IEvent result)
            throw new InvalidOperationException(
                $"Upcaster for '{EventTypeId}' produced a '{current.GetType().Name}' " +
                $"which does not implement {nameof(IEvent)}. Ensure the final step's " +
                "transform returns an IEvent implementation.");

        return result;
    }
}
