namespace Alberto.Upcasting;

/// <summary>
/// Holds the complete set of upcast chains for a module.
/// Pass an instance to <see cref="EventSerializer.WithUpcasters"/> so that
/// <see cref="EventSerializer.Deserialize"/> applies the appropriate chain
/// whenever it reads a versioned event whose envelope version is lower than
/// the chain's <see cref="UpcasterDeclaration.CurrentVersion"/>.
/// </summary>
/// <example>
/// <code>
/// var upcasters = UpcasterRegistry.Create()
///     .Add(DeclareUpcaster
///         .For&lt;OrderPlaced&gt;("order-placed")
///         .From&lt;OrderPlacedV1&gt;(1, v1 => new OrderPlaced(v1.OrderId, v1.Amount, "default"))
///         .Build())
///     .Build();
///
/// var serializer = EventSerializer
///     .FromAssembly(Assembly.GetExecutingAssembly())
///     .WithUpcasters(upcasters);
/// </code>
/// </example>
public sealed class UpcasterRegistry
{
    private readonly IReadOnlyDictionary<string, UpcasterDeclaration> _declarations;

    private UpcasterRegistry(IReadOnlyDictionary<string, UpcasterDeclaration> declarations)
        => _declarations = declarations;

    /// <summary>Starts building a new registry.</summary>
    public static UpcasterRegistryBuilder Create() => new();

    /// <summary>
    /// Looks up an upcaster by event type ID.
    /// </summary>
    internal bool TryGet(string eventTypeId, out UpcasterDeclaration? declaration)
        => _declarations.TryGetValue(eventTypeId, out declaration);

    // ---------------------------------------------------------------------------
    // Builder
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Fluent builder for <see cref="UpcasterRegistry"/>.
    /// </summary>
    public sealed class UpcasterRegistryBuilder
    {
        private readonly Dictionary<string, UpcasterDeclaration> _declarations = [];

        /// <summary>
        /// Registers an upcast declaration. Each event type ID may appear at most once.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when an upcaster for the same event type ID has already been added.
        /// </exception>
        public UpcasterRegistryBuilder Add(UpcasterDeclaration declaration)
        {
            ArgumentNullException.ThrowIfNull(declaration);

            if (_declarations.ContainsKey(declaration.EventTypeId))
                throw new InvalidOperationException(
                    $"An upcaster for event type '{declaration.EventTypeId}' is already registered.");

            _declarations[declaration.EventTypeId] = declaration;
            return this;
        }

        /// <summary>Returns the completed <see cref="UpcasterRegistry"/>.</summary>
        public UpcasterRegistry Build() => new(_declarations);
    }
}
