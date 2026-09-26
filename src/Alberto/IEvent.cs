using System.Diagnostics.CodeAnalysis;

namespace Alberto;

/// <summary>
/// Marker interface for domain events in the DCB event store.
/// All events that are stored and queried must implement this interface.
/// </summary>
/// <remarks>
/// Every event type keeps its public properties under trimming: that is where
/// <see cref="TagAttribute"/>s live, and where tag extraction reads them from when an event type
/// is not described by a registry that supplies its own extractor.
/// </remarks>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public interface IEvent;
