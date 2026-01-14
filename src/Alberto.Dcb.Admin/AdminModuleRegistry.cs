namespace Alberto.Dcb.Admin;

/// <summary>
/// Registry of modules that have admin enabled.
/// </summary>
public sealed class AdminModuleRegistry
{
    private readonly Dictionary<string, AdminOptions> _modules = new();

    /// <summary>
    /// All registered modules and their options.
    /// </summary>
    public IReadOnlyDictionary<string, AdminOptions> Modules => _modules;

    /// <summary>
    /// Register a module with admin options.
    /// </summary>
    internal void RegisterModule(string moduleKey, AdminOptions options)
    {
        _modules[moduleKey] = options;
    }
}
