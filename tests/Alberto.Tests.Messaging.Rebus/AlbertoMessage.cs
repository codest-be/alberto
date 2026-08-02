namespace Alberto.Tests.Messaging.Rebus;

/// <summary>
/// Envelope carrying one Alberto outbox entry to a Rebus handler.
/// </summary>
/// <remarks>
/// Rebus dispatches handlers by the .NET type of the message body, and Alberto hands the
/// transport a JSON string rather than an object, so the string needs a type to travel in.
/// With Rebus's default serializer this envelope is what goes on the wire, and
/// <see cref="Payload"/> arrives as a string field the consumer parses itself. Registering
/// <see cref="AlbertoOutboxSerializer"/> on the bus changes that; see its remarks.
/// </remarks>
/// <param name="MessageType">Alberto's logical message type, for example <c>order.placed</c>.</param>
/// <param name="Version">Schema version of <paramref name="Payload"/>.</param>
/// <param name="Payload">Already-serialized JSON, exactly as the outbox stored it.</param>
public sealed record AlbertoMessage(string MessageType, string Version, string Payload);
