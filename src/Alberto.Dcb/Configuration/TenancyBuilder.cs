namespace Alberto.Dcb.Configuration;

/// <summary>
/// Declares how a module's tenants are laid out. Reached through
/// <c>.WithTenancy(t =&gt; ...)</c>; a module that calls <c>.WithTenancy()</c> without an argument
/// never sees this type and keeps every tenant in one database.
/// </summary>
/// <remarks>
/// Like every other Alberto builder this only accumulates a declaration. Backend packages add
/// their own entry points as extension methods — <c>AcrossPostgresDatabases</c> lives in
/// <c>Alberto.Dcb.Postgres</c> — so the core has no knowledge of what a shard is stored in.
/// </remarks>
public sealed class TenancyBuilder
{
    internal TenancyBuilder(string moduleKey, IAlbertoBackendDescriptor? moduleBackend)
    {
        ModuleKey = moduleKey;
        ModuleBackend = moduleBackend;
    }

    /// <summary>The key of the module being configured. Useful in error messages.</summary>
    public string ModuleKey { get; }

    /// <summary>
    /// The module's own backend declaration, or null when none was declared yet. Shard builders
    /// use it as the template a shard's options start from.
    /// </summary>
    public IAlbertoBackendDescriptor? ModuleBackend { get; }

    internal TenancyDefinition Definition { get; private set; } = new();

    /// <summary>
    /// Applies <paramref name="configure"/> to this module's tenancy declaration. The single
    /// mutation primitive; backend extension methods are built on it.
    /// </summary>
    public TenancyBuilder Configure(Func<TenancyDefinition, TenancyDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Definition = configure(Definition)
            ?? throw new InvalidOperationException("A tenancy configuration callback returned null.");

        return this;
    }
}
