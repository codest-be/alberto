namespace Alberto.Admin;

/// <summary>
/// One Alberto event store the admin surface can address.
/// </summary>
/// <remarks>
/// A host registers one of these per module it wants operable. The factories are
/// resolved lazily and cached by <see cref="AdminModuleRegistry"/>, so a host that
/// registers ten modules pays for the ones an operator actually opens.
/// </remarks>
/// <param name="Key">
/// The module key, matching the key the module was registered with via <c>AddAlberto</c>.
/// This is what a GraphQL caller passes as <c>module</c>.
/// </param>
/// <param name="Schema">The database schema the module's tables live in.</param>
/// <param name="IsDefault">
/// Whether this module answers when the caller names none. Exactly one registration
/// should set this.
/// </param>
/// <param name="ReaderFactory">Builds the reader for this module.</param>
/// <param name="OperatorFactory">Builds the operator for this module.</param>
public sealed record AdminModuleRegistration(
    string Key,
    string Schema,
    bool IsDefault,
    Func<IServiceProvider, IAdminReader> ReaderFactory,
    Func<IServiceProvider, IAdminOperator> OperatorFactory);

/// <summary>Describes a registered admin module, without the factories.</summary>
public sealed record AdminModuleDescriptor(string Key, string Schema, bool IsDefault);

/// <summary>
/// Resolves the admin reader and operator for a named module.
/// </summary>
/// <remarks>
/// A host can register several Alberto modules side by side — the Orders example runs
/// <c>orders</c> and <c>payments</c> in one process. Each module is its own event store
/// with its own schema, so <b>positions from two modules are unrelated numbers and must
/// never be compared</b>. That is why this is a registry the caller picks from rather
/// than a merged view: there is no meaningful union of two event logs.
/// </remarks>
public interface IAdminModuleRegistry
{
    /// <summary>Every registered module, default first, then by key.</summary>
    IReadOnlyList<AdminModuleDescriptor> Modules { get; }

    /// <summary>The module used when a caller names none.</summary>
    AdminModuleDescriptor Default { get; }

    /// <summary>
    /// Returns the reader for <paramref name="moduleKey"/>, or the default module's reader
    /// when the key is null or blank.
    /// </summary>
    /// <exception cref="UnknownAdminModuleException">The key names no registered module.</exception>
    IAdminReader GetReader(string? moduleKey);

    /// <summary>
    /// Returns the operator for <paramref name="moduleKey"/>, or the default module's operator
    /// when the key is null or blank.
    /// </summary>
    /// <exception cref="UnknownAdminModuleException">The key names no registered module.</exception>
    IAdminOperator GetOperator(string? moduleKey);

    /// <summary>
    /// Returns the key that <paramref name="moduleKey"/> resolves to — itself when it names a
    /// registered module, the default key when it is null or blank.
    /// </summary>
    /// <exception cref="UnknownAdminModuleException">The key names no registered module.</exception>
    string ResolveKey(string? moduleKey);
}

/// <summary>Thrown when a caller names a module the host did not register.</summary>
public sealed class UnknownAdminModuleException : Exception
{
    public UnknownAdminModuleException(string moduleKey, IEnumerable<string> knownKeys)
        : base($"Unknown admin module '{moduleKey}'. Registered modules: {string.Join(", ", knownKeys)}.")
    {
        ModuleKey = moduleKey;
    }

    /// <summary>The key that was not found.</summary>
    public string ModuleKey { get; }
}

/// <inheritdoc />
public sealed class AdminModuleRegistry : IAdminModuleRegistry
{
    private readonly Dictionary<string, Lazy<IAdminReader>> _readers;
    private readonly Dictionary<string, Lazy<IAdminOperator>> _operators;
    private readonly string _defaultKey;

    public AdminModuleRegistry(IEnumerable<AdminModuleRegistration> registrations, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(services);

        var list = registrations.ToList();
        if (list.Count == 0)
        {
            throw new InvalidOperationException(
                "No admin modules are registered. Call AddAlbertoPostgresAdmin(...) for at least one module.");
        }

        var duplicate = list.GroupBy(r => r.Key, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Admin module '{duplicate.Key}' is registered more than once.");
        }

        // Falling back to the first registration rather than throwing keeps a host that
        // registers a single module from having to say which one is the default.
        _defaultKey = (list.FirstOrDefault(r => r.IsDefault) ?? list[0]).Key;

        _readers = list.ToDictionary(
            r => r.Key,
            r => new Lazy<IAdminReader>(() => r.ReaderFactory(services)),
            StringComparer.Ordinal);
        _operators = list.ToDictionary(
            r => r.Key,
            r => new Lazy<IAdminOperator>(() => r.OperatorFactory(services)),
            StringComparer.Ordinal);

        Modules = list
            .Select(r => new AdminModuleDescriptor(r.Key, r.Schema, r.Key == _defaultKey))
            .OrderByDescending(m => m.IsDefault)
            .ThenBy(m => m.Key, StringComparer.Ordinal)
            .ToList();

        Default = Modules.First(m => m.IsDefault);
    }

    /// <inheritdoc />
    public IReadOnlyList<AdminModuleDescriptor> Modules { get; }

    /// <inheritdoc />
    public AdminModuleDescriptor Default { get; }

    /// <inheritdoc />
    public IAdminReader GetReader(string? moduleKey) => _readers[ResolveKey(moduleKey)].Value;

    /// <inheritdoc />
    public IAdminOperator GetOperator(string? moduleKey) => _operators[ResolveKey(moduleKey)].Value;

    /// <inheritdoc />
    public string ResolveKey(string? moduleKey)
    {
        if (string.IsNullOrWhiteSpace(moduleKey))
            return _defaultKey;

        if (!_readers.ContainsKey(moduleKey))
            throw new UnknownAdminModuleException(moduleKey, _readers.Keys);

        return moduleKey;
    }
}
