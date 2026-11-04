using Alberto.Subscriptions;

namespace Alberto.Configuration;

/// <summary>What kind of work a declared processor performs.</summary>
public enum ProcessorKind
{
    /// <summary>Folds events into a queryable read model.</summary>
    Projection = 0,

    /// <summary>Reacts to events with a side effect.</summary>
    Reactor = 1,
}

/// <summary>
/// A processor as declared at configuration time. This is the validator's view of the module:
/// it names every processor without resolving anything from the container.
/// </summary>
public sealed record ProcessorDeclaration
{
    /// <summary>The checkpoint key. Unique within a module.</summary>
    public required string ProcessorId { get; init; }

    /// <summary>Whether this is a projection or a reactor.</summary>
    public required ProcessorKind Kind { get; init; }

    /// <summary>How the control loop should dispatch to this processor.</summary>
    public ProcessorExecutionOptions Execution { get; init; } = ProcessorExecutionOptions.Default;

    /// <summary>
    /// Whether a failing event halts the processor instead of being dead-lettered. Always true
    /// for a <see cref="ProcessorKind.Projection"/>. A reactor sets it when the order of its
    /// effects matters more than moving on, as the outbox handler does. Halted processors have
    /// no dead letters and no redelivery; see <see cref="HaltOptions"/>.
    /// </summary>
    public bool HaltsOnFailure { get; init; }

    /// <summary>
    /// The handler type the processor id was derived from, when there is one.
    /// Null for processors registered from a bare lambda.
    /// </summary>
    public Type? HandlerType { get; init; }
}
