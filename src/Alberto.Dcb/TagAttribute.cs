namespace Alberto.Dcb;

/// <summary>
/// Marks a property as a tag for event routing and querying.
/// Tags follow the DCB (Dynamic Consistency Boundary) pattern where
/// events are tagged with concept:value pairs (e.g., "order:123", "customer:456").
/// </summary>
/// <param name="concept">The tag concept (e.g., "order", "customer", "venue").</param>
/// <example>
/// <code>
/// [EventType("order-placed")]
/// public record OrderPlaced(
///     [property: Tag("order")] Guid OrderId,
///     [property: Tag("customer")] Guid CustomerId,
///     decimal Amount) : IEvent;
/// </code>
/// </example>
/// <remarks>
/// On record primary-constructor parameters, use the explicit <c>[property: Tag(...)]</c> target
/// specifier so the attribute lands on the synthesised property, which is where tag extraction
/// reads from. Writing <c>[Tag(...)]</c> without a target on a parameter was previously accepted
/// but silently produced no tags; that misuse now fails at compile time.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TagAttribute(string concept) : Attribute
{
    /// <summary>
    /// The tag concept (e.g., "order", "customer").
    /// The property value will be used as the tag value.
    /// </summary>
    public string Concept { get; } = concept;
}
