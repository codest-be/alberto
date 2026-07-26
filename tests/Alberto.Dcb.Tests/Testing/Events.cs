using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.Tests.Testing;

// ---------------------------------------------------------------------------
// Canonical test event vocabulary.
//
// These records are the authoritative copies shared across all test files.
// Eight files currently keep their own duplicate declarations; SP1b will
// migrate those files onto this vocabulary — do not migrate them here to
// avoid a merge conflict with that sweep.
// ---------------------------------------------------------------------------

/// <summary>Raised when a new order is placed.</summary>
[EventType("order-created")]
public record OrderCreated(Guid OrderId, decimal Amount) : IEvent;

/// <summary>Raised when an order moves to the confirmed state.</summary>
[EventType("order-confirmed")]
public record OrderConfirmed(Guid OrderId) : IEvent;

/// <summary>Raised when an order is cancelled.</summary>
[EventType("order-cancelled")]
public record OrderCancelled(Guid OrderId) : IEvent;

/// <summary>Raised when a free-text note is appended to an order.</summary>
[EventType("order-note-added")]
public record OrderNoteAdded(Guid OrderId, string Note) : IEvent;

// ---------------------------------------------------------------------------
// Canonical test state types
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal per-order state that tracks only the running amount.
/// Used by harness tests and projection conformance suites.
/// </summary>
public record OrderTotal
{
    /// <summary>The unique identifier of the order.</summary>
    public Guid OrderId { get; init; }

    /// <summary>The monetary amount associated with the order.</summary>
    public decimal Amount { get; init; }
}

/// <summary>
/// Richer per-order summary that tracks the order status as well as the amount.
/// Used by projection specification tests.
/// </summary>
public record OrderSummary
{
    /// <summary>The unique identifier of the order.</summary>
    public Guid OrderId { get; init; }

    /// <summary>The monetary amount associated with the order.</summary>
    public decimal Amount { get; init; }

    /// <summary>Human-readable status string (e.g. "Created", "Confirmed").</summary>
    public string Status { get; init; } = "";
}
