namespace Alberto.Dcb.Tests.SampleEvents;

// This assembly exists for one reason: to be a realistic argument to
// .WithEventsFrom(...).
//
// EventSerializer.FromAssembly builds a slug -> Type registry and throws on a
// duplicate slug. The main test assembly declares many [EventType] types across
// unrelated fixtures and collides with itself, so no test in it can pass its own
// assembly to WithEventsFrom. That is why the end-to-end configuration path went
// unverified while the upcaster feature was, in fact, impossible to switch on:
// every upcaster test built the EventSerializer by hand.
//
// Keep this assembly small and its slugs unique. Types here are scanned wholesale.

/// <summary>
/// v1 shape of the versioned order event — no <c>Country</c>.
/// Not itself an <see cref="IEvent"/>: it is only ever the input side of an
/// upcaster step, reconstructed from stored JSON.
/// </summary>
public sealed record VersionedOrderV1(Guid OrderId, decimal Amount);

/// <summary>
/// Current shape of the versioned order event. <c>Country</c> did not exist at v1,
/// so events stored before the bump must be upcast before any handler sees them.
/// </summary>
[EventType("e2e-versioned-order", Version = 2)]
public sealed record VersionedOrder(
    [property: Tag("order")] Guid OrderId,
    decimal Amount,
    string Country) : IEvent;

/// <summary>
/// An unversioned event in the same assembly, so the registry under test is not
/// degenerate — assembly scanning has to cope with a mix.
/// </summary>
[EventType("e2e-plain-note")]
public sealed record PlainNote(
    [property: Tag("note")] Guid NoteId,
    string Text) : IEvent;
