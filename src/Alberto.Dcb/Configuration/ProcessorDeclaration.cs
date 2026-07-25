using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.Configuration;

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
    /// The handler type the processor id was derived from, when there is one.
    /// Null for processors registered from a bare lambda.
    /// </summary>
    public Type? HandlerType { get; init; }
}
