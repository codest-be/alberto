namespace Alberto.Subscriptions;

/// <summary>
/// Internal lifecycle-control surface for event processors.
/// Exposes the setters that consumers must not call: only the framework (control loop,
/// rebuild coordinator, and their shadow wrappers) should be able to activate or deactivate
/// a processor or mark it as rebuilding.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IEventProcessor"/> exposes <c>IsActive</c> and <c>IsRebuilding</c> as read-only
/// properties so that external code can observe processor state without being able to mutate it.
/// This interface is the complementary write side, kept internal so that application code cannot
/// reach it directly.
/// </para>
/// <para>
/// A processor that does not implement this interface can still be hosted — the control loop
/// checks state by casting and skips the set if the cast fails. The only consequence is that
/// nothing can deactivate or rebuild-flag such a processor from the outside.
/// </para>
/// </remarks>
internal interface IProcessorLifecycle
{
    /// <summary>Sets whether the processor should accept and process events.</summary>
    bool IsActive { get; set; }

    /// <summary>Sets whether the processor is in rebuild (catch-up) mode.</summary>
    bool IsRebuilding { get; set; }
}
