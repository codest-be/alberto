using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Dcb.EntityFramework.Batching;

/// <summary>
/// Interface for batch handlers that have direct DbContext access.
/// Enables complex multi-table projections with a single SaveChanges per batch.
/// </summary>
/// <typeparam name="TDbContext">The DbContext type to use.</typeparam>
public interface IEfBatchHandler<in TDbContext> where TDbContext : DbContext
{
    /// <summary>
    /// The event types this handler processes.
    /// </summary>
    IReadOnlySet<string> HandledEventTypes { get; }

    /// <summary>
    /// Applies an event to the DbContext.
    /// The context accumulates changes; SaveChanges is called once per batch.
    /// </summary>
    /// <param name="context">The DbContext to apply changes to.</param>
    /// <param name="event">The event to process.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ApplyAsync(TDbContext context, IEventEnvelope @event, CancellationToken ct);
}
