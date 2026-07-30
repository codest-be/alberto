namespace Alberto.Examples.Shared;

/// <summary>
/// Result of a mutation that doesn't return data.
/// </summary>
/// <remarks>
/// Shared by both example modules rather than duplicated into each, because a GraphQL schema has
/// one type per name: two <c>MutationResult</c> records in two assemblies would collide when the
/// host merges the modules, and renaming either one would change the schema clients see.
/// <para>
/// This is a transport shape, not domain state. Nothing here knows what an order or a payment is,
/// which is why sharing it does not put back the shared state the slices exist to remove.
/// </para>
/// </remarks>
public readonly record struct MutationResult
{
    public bool Success => true;
}

/// <summary>
/// Error result for failed mutations.
/// </summary>
public sealed record MutationError(string Message);
