namespace Alberto.Dcb.Postgres;

/// <summary>
/// Helper for qualifying SQL identifiers with schema prefix.
/// </summary>
internal sealed class SchemaQualifier
{
    private readonly string _prefix;

    /// <summary>
    /// Creates a new schema qualifier.
    /// </summary>
    /// <param name="schema">The schema name, or null/empty for default (public) schema.</param>
    public SchemaQualifier(string? schema)
    {
        _prefix = string.IsNullOrWhiteSpace(schema) ? "" : $"{schema}.";
    }

    /// <summary>
    /// Gets the schema prefix (e.g., "orders." or "").
    /// </summary>
    public string Prefix => _prefix;

    /// <summary>
    /// Qualifies a table name with the schema prefix.
    /// </summary>
    public string Table(string tableName) => $"{_prefix}{tableName}";

    /// <summary>
    /// Qualifies a function name with the schema prefix.
    /// </summary>
    public string Function(string functionName) => $"{_prefix}{functionName}";

    /// <summary>
    /// Whether a custom schema is configured.
    /// </summary>
    public bool HasSchema => !string.IsNullOrEmpty(_prefix);
}
